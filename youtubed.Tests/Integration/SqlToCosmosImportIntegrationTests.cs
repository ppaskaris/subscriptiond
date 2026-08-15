using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using youtubed.DataTransfer;
using youtubed.Data;
using youtubed.Domain;
using youtubed.Models;
using youtubed.Persistence;
using youtubed.Persistence.Cosmos;
using youtubed.Services;
using youtubed.Tests.Infrastructure;

namespace youtubed.Tests.Integration
{
    [Trait("Category", "Cosmos")]
    public sealed class SqlToCosmosImportIntegrationTests
    {
        private readonly ITestOutputHelper _output;

        public SqlToCosmosImportIntegrationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [CosmosFact]
        public async Task Import_RecoversAfterDurableInterruptionAndMatchesProviderBehavior()
        {
            var source = new LocalDbTestFixture();
            var target = new CosmosTestFixture();
            var secondTarget = new CosmosTestFixture();
            await source.InitializeAsync();
            await target.InitializeAsync();
            await secondTarget.InitializeAsync();

            try
            {
                var importedAt = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
                var migrationAt = importedAt.AddMinutes(76);
                var migrationClock = new FakeAppClock
                {
                    UtcNow = importedAt
                };
                var listId = Guid.NewGuid();
                var expiredListId = Guid.NewGuid();
                var token = Enumerable.Range(1, 40).Select(value => (byte)value).ToArray();
                await SeedSourceAsync(source, importedAt, listId, expiredListId, token);
                var tokenString = WebEncoders.Base64UrlEncode(token);
                var importedListPath = $"/{tokenString}/list/{listId:D}";

                using (var drainFactory = RehearsalWebApplicationFactory.ForSql(
                    source.ConnectionString,
                    migrationClock,
                    shareCreationEnabled: false))
                using (var drainClient = drainFactory.CreateClient(new WebApplicationFactoryClientOptions
                {
                    AllowAutoRedirect = false
                }))
                {
                    Assert.IsType<ListRepository>(
                        drainFactory.Services.GetRequiredService<IListRepository>());
                    using var sharePage = await drainClient.GetAsync(importedListPath + "/share");
                    Assert.Equal(HttpStatusCode.OK, sharePage.StatusCode);
                    var shareContent = await sharePage.Content.ReadAsStringAsync();
                    Assert.Contains("New share links are temporarily unavailable", shareContent, StringComparison.Ordinal);
                    Assert.DoesNotContain("title=\"Create share link\"", shareContent, StringComparison.Ordinal);

                    var linksBefore = await drainFactory.Services
                        .GetRequiredService<IShareLinkRepository>()
                        .GetByListAsync(listId);
                    Assert.Single(linksBefore);
                    using var blockedCreate = await drainClient.PostAsync(
                        importedListPath + "/share/create",
                        EmptyForm());
                    Assert.Equal(HttpStatusCode.ServiceUnavailable, blockedCreate.StatusCode);
                    Assert.Single(await drainFactory.Services
                        .GetRequiredService<IShareLinkRepository>()
                        .GetByListAsync(listId));

                    Assert.Equal(1, await CountValidUnconsumedShareLinksAsync(source, importedAt));
                    migrationClock.UtcNow = migrationAt;
                    Assert.Equal(0, await CountValidUnconsumedShareLinksAsync(source, migrationAt));
                }

                var writesStoppedAtUtc = migrationAt;
                var downtimeStopwatch = Stopwatch.StartNew();

                var importTarget = new CosmosImportTarget(target.Context);
                using var output = new StringWriter();
                SqlToCosmosImportResult firstReconciliation = null;
                var firstRehearsalStopwatch = Stopwatch.StartNew();
                for (var interruptionPoint = 1; interruptionPoint <= 3; interruptionPoint++)
                {
                    if (interruptionPoint > 1)
                    {
                        await ClearTargetAsync(target, listId);
                    }

                    var interrupted = new SqlToCosmosImportService(
                        new SqlImportSource(source.ConnectionString),
                        new InterruptAfterDurableWriteTarget(importTarget, interruptionPoint),
                        output,
                        migrationClock);
                    await Assert.ThrowsAsync<SimulatedInterruptionException>(() => interrupted.RunAsync(
                        new SqlToCosmosImportOptions(
                            SqlToCosmosImportMode.Import,
                            2,
                            ConfirmEmptyTarget: true,
                            ConfirmPreCutoverRerun: false),
                        migrationAt,
                        CancellationToken.None));

                    var recovery = new SqlToCosmosImportService(
                        new SqlImportSource(source.ConnectionString),
                        importTarget,
                        output,
                        migrationClock);
                    await recovery.RunAsync(
                        new SqlToCosmosImportOptions(
                            SqlToCosmosImportMode.Import,
                            2,
                            ConfirmEmptyTarget: false,
                            ConfirmPreCutoverRerun: true),
                        migrationAt,
                        CancellationToken.None);
                    firstReconciliation = await recovery.RunAsync(
                        new SqlToCosmosImportOptions(
                            SqlToCosmosImportMode.Reconcile,
                            2,
                            ConfirmEmptyTarget: false,
                            ConfirmPreCutoverRerun: false),
                        migrationAt,
                        CancellationToken.None);
                }
                firstRehearsalStopwatch.Stop();
                var firstRehearsalMetrics = importTarget.Metrics;

                var secondRehearsalStopwatch = Stopwatch.StartNew();
                var secondImportTarget = new CosmosImportTarget(secondTarget.Context);
                var secondRehearsal = new SqlToCosmosImportService(
                    new SqlImportSource(source.ConnectionString),
                    secondImportTarget,
                    output,
                    migrationClock);
                var validation = await secondRehearsal.RunAsync(
                    new SqlToCosmosImportOptions(
                        SqlToCosmosImportMode.Validate,
                        2,
                        ConfirmEmptyTarget: false,
                        ConfirmPreCutoverRerun: false),
                    migrationAt,
                    CancellationToken.None);
                await secondRehearsal.RunAsync(
                    new SqlToCosmosImportOptions(
                        SqlToCosmosImportMode.Import,
                        2,
                        ConfirmEmptyTarget: true,
                        ConfirmPreCutoverRerun: false),
                    migrationAt,
                    CancellationToken.None);
                var secondReconciliation = await secondRehearsal.RunAsync(
                    new SqlToCosmosImportOptions(
                        SqlToCosmosImportMode.Reconcile,
                        2,
                        ConfirmEmptyTarget: false,
                        ConfirmPreCutoverRerun: false),
                    migrationAt,
                    CancellationToken.None);
                secondRehearsalStopwatch.Stop();
                var secondRehearsalMetrics = secondImportTarget.Metrics;

                Assert.NotNull(firstReconciliation);
                Assert.Equal(validation.ReconciliationHash, firstReconciliation.ReconciliationHash);
                Assert.Equal(firstReconciliation.ReconciliationHash, secondReconciliation.ReconciliationHash);
                Assert.Equal(firstReconciliation.ListCount, secondReconciliation.ListCount);
                Assert.Equal(firstReconciliation.ChannelCount, secondReconciliation.ChannelCount);

                var expectedDocument = Assert.Single(await ReadAllAsync(
                    new SqlImportSource(source.ConnectionString).ReadListsAsync(
                        migrationAt,
                        2,
                        CancellationToken.None)));
                var actualDocument = Assert.Single(await ReadAllAsync(
                    importTarget.ReadListsAsync(2, CancellationToken.None)));
                Assert.Equal(expectedDocument.Id, actualDocument.Id);
                Assert.Equal(expectedDocument.Token, actualDocument.Token);
                Assert.Equal(expectedDocument.Title, actualDocument.Title);
                Assert.Equal(expectedDocument.PlaybackRate, actualDocument.PlaybackRate);
                Assert.Equal(expectedDocument.ExpiredAfter, actualDocument.ExpiredAfter);
                Assert.Equal(expectedDocument.ExpirationRenewedOn, actualDocument.ExpirationRenewedOn);
                Assert.Equal(expectedDocument.ChannelIds, actualDocument.ChannelIds);
                Assert.Equal(expectedDocument.Ttl, actualDocument.Ttl);
                Assert.NotNull(actualDocument.ETag);
                var lists = new CosmosListRepository(
                    target.Context,
                    migrationClock,
                    new CosmosRequestRecorder<CosmosListRepository>());
                var channels = new CosmosChannelRepository(
                    target.Context,
                    new CosmosRequestRecorder<CosmosChannelRepository>());
                var importedList = await lists.GetAsync(listId);
                Assert.NotNull(importedList);
                Assert.Equal(token, importedList.Token);
                Assert.Equal("Representative list", importedList.Title);
                Assert.Equal(1.50m, importedList.PlaybackRate);
                Assert.Equal(importedAt.Add(Constants.ListMaxAgeMin), importedList.ExpiredAfter);
                Assert.Equal(DateOnly.FromDateTime(importedAt.UtcDateTime), importedList.ExpirationRenewedOn);

                var projection = await lists.GetChannelProjectionAsync(importedList);
                Assert.Equal(new[] { "channel-active", "channel-unavailable" }, projection.ChannelIds);
                Assert.All(projection.Channels, channel => Assert.False(channel.IsMissing));

                var unavailable = await channels.GetByIdAsync("channel-unavailable");
                Assert.Equal(ChannelStatus.Unavailable, unavailable.Status);
                Assert.Equal(ChannelStatusReason.NotFound, unavailable.StatusReason);
                Assert.Equal(importedAt.AddHours(-3), unavailable.StatusUpdatedAt);
                Assert.Equal(importedAt.AddHours(-2), unavailable.StaleAfter);

                var active = await channels.GetByIdAsync("channel-active");
                Assert.Equal(100, active.Videos.Count);
                Assert.Equal("video-100", active.Videos[0].VideoId);
                Assert.Equal("video-001", active.Videos[^1].VideoId);

                Assert.Null(await lists.GetAsync(expiredListId));
                Assert.Null(await channels.GetByIdAsync("expired-only-channel"));
                Assert.Null(await channels.GetByIdAsync("unreferenced-channel"));
                Assert.Equal(0, await importTarget.CountShareLinksAsync(CancellationToken.None));

                var listDocument = await target.Context.Lists.ReadItemAsync<CosmosListDocument>(
                    listId.ToString("D"),
                    new Microsoft.Azure.Cosmos.PartitionKey(listId.ToString("D")));
                Assert.InRange(
                    listDocument.Resource.Ttl,
                    checked((int)TimeSpan.FromDays(44).TotalSeconds),
                    checked((int)Constants.ListMaxAgeMin.TotalSeconds));
                Assert.True(
                    CosmosSystemTextJsonSerializer.Instance.GetSerializedUtf8Size(listDocument.Resource) < 512 * 1024);
                var channelDocument = await target.Context.Channels.ReadItemAsync<CosmosChannelDocument>(
                    "channel-active",
                    new Microsoft.Azure.Cosmos.PartitionKey("channel-active"));
                Assert.True(
                    CosmosSystemTextJsonSerializer.Instance.GetSerializedUtf8Size(channelDocument.Resource) < 512 * 1024);

                migrationClock.UtcNow = migrationAt.AddDays(1);
                var youtube = new FakeYoutubeService();
                youtube.SetChannelById("channel-active", new YoutubeChannel
                {
                    Id = "channel-active",
                    Title = "Refreshed active channel",
                    Thumbnail = "https://example.test/refreshed-active.jpg",
                    PlaylistId = "playlist-active"
                });
                youtube.SetVideos("playlist-active", new YoutubeVideo
                {
                    ChannelId = "channel-active",
                    Id = "refreshed-video",
                    Title = "Refreshed video",
                    Duration = TimeSpan.FromMinutes(4),
                    PublishedAt = migrationAt.AddMinutes(-5),
                    Thumbnail = "https://example.test/refreshed-video.jpg"
                });
                const string addedChannelId = "UC-rehearsal-added";
                const string addedPlaylistId = "UU-rehearsal-added";
                const string addedChannelUrl = "https://www.youtube.com/channel/UC-rehearsal-added";
                youtube.SetChannel(addedChannelUrl, new YoutubeChannel
                {
                    Id = addedChannelId,
                    Title = "Added channel",
                    Thumbnail = "https://example.test/added.jpg",
                    PlaylistId = addedPlaylistId
                });
                youtube.SetVideos(addedPlaylistId, new YoutubeVideo
                {
                    ChannelId = addedChannelId,
                    Id = "added-video",
                    Title = "Added video",
                    Duration = TimeSpan.FromMinutes(3),
                    PublishedAt = migrationAt.AddMinutes(-2),
                    Thumbnail = "https://example.test/added-video.jpg"
                });

                var smokeStopwatch = Stopwatch.StartNew();
                RehearsalWebApplicationFactory cosmosFactory;
                using (cosmosFactory = RehearsalWebApplicationFactory.ForCosmos(
                    CosmosEmulatorOptions.FromEnvironment().ConnectionString,
                    target.DatabaseName,
                    migrationClock,
                    youtube))
                using (var cosmosClient = cosmosFactory.CreateClient(new WebApplicationFactoryClientOptions
                {
                    AllowAutoRedirect = false
                }))
                {
                    Assert.IsType<CosmosListRepository>(
                        cosmosFactory.Services.GetRequiredService<IListRepository>());
                    using var authenticatedResponse = await cosmosClient.GetAsync(importedListPath);
                    Assert.Equal(HttpStatusCode.OK, authenticatedResponse.StatusCode);
                    var authenticatedContent = await authenticatedResponse.Content.ReadAsStringAsync();
                    Assert.Contains("Representative list", authenticatedContent, StringComparison.Ordinal);
                    Assert.Contains("video-100", authenticatedContent, StringComparison.Ordinal);

                    var hostLists = cosmosFactory.Services.GetRequiredService<IListRepository>();
                    var renewedList = await hostLists.GetAsync(listId);
                    Assert.Equal(migrationClock.UtcToday, renewedList.ExpirationRenewedOn);
                    Assert.Equal(
                        migrationClock.UtcNow.Add(Constants.ListMaxAgeMin),
                        renewedList.ExpiredAfter);
                    var renewedDocument = await target.Context.Lists.ReadItemAsync<CosmosListDocument>(
                        listId.ToString("D"),
                        new Microsoft.Azure.Cosmos.PartitionKey(listId.ToString("D")));
                    Assert.InRange(
                        renewedDocument.Resource.Ttl,
                        checked((int)TimeSpan.FromDays(44).TotalSeconds),
                        checked((int)TimeSpan.FromDays(47).TotalSeconds));
                    var hostProjection = await hostLists.GetChannelProjectionAsync(renewedList);
                    Assert.Equal(
                        new[] { "channel-active", "channel-unavailable" },
                        hostProjection.ChannelIds);
                    var hostUnavailable = Assert.Single(
                        hostProjection.Channels,
                        channel => channel.Id == "channel-unavailable");
                    Assert.Equal(ChannelStatus.Unavailable, hostUnavailable.Status);
                    Assert.Equal(ChannelStatusReason.NotFound, hostUnavailable.StatusReason);

                    await WaitUntilAsync(async () =>
                        string.Equals(
                            (await cosmosFactory.Services
                                .GetRequiredService<IChannelRepository>()
                                .GetByIdAsync("channel-active"))?.Title,
                            "Refreshed active channel",
                            StringComparison.Ordinal));
                    using var refreshedResponse = await cosmosClient.GetAsync(importedListPath);
                    Assert.Contains(
                        "Refreshed video",
                        await refreshedResponse.Content.ReadAsStringAsync(),
                        StringComparison.Ordinal);

                    using var addResponse = await cosmosClient.PostAsync(
                        importedListPath + "/add-channel",
                        Form(("Url", addedChannelUrl)));
                    Assert.Equal(HttpStatusCode.Redirect, addResponse.StatusCode);
                    await WaitUntilAsync(async () =>
                        (await hostLists.GetChannelProjectionAsync(await hostLists.GetAsync(listId)))
                            .ChannelIds.Contains(addedChannelId, StringComparer.Ordinal));
                    await WaitUntilAsync(async () =>
                        (await cosmosFactory.Services
                            .GetRequiredService<IChannelRepository>()
                            .GetByIdAsync(addedChannelId))?.Videos.Any(video => video.VideoId == "added-video") == true);

                    using var removeResponse = await cosmosClient.PostAsync(
                        importedListPath + "/remove-channel",
                        Form(("ChannelId", addedChannelId)));
                    Assert.Equal(HttpStatusCode.Redirect, removeResponse.StatusCode);
                    Assert.DoesNotContain(
                        addedChannelId,
                        (await hostLists.GetChannelProjectionAsync(await hostLists.GetAsync(listId))).ChannelIds);

                    var lookupCallsBeforeReadd = youtube.GetChannelCallCount;
                    using var readdResponse = await cosmosClient.PostAsync(
                        importedListPath + "/add-channel",
                        Form(("Url", addedChannelUrl)));
                    Assert.Equal(HttpStatusCode.Redirect, readdResponse.StatusCode);
                    Assert.Equal(lookupCallsBeforeReadd, youtube.GetChannelCallCount);
                    Assert.Contains(
                        addedChannelId,
                        (await hostLists.GetChannelProjectionAsync(await hostLists.GetAsync(listId))).ChannelIds);

                    var refreshCallsBeforeForce = youtube.GetChannelsByIdCallCount;
                    using var forceRefreshResponse = await cosmosClient.GetAsync(importedListPath + "/refresh");
                    Assert.Equal(HttpStatusCode.Redirect, forceRefreshResponse.StatusCode);
                    await WaitUntilAsync(() => Task.FromResult(
                        youtube.GetChannelsByIdCallCount > refreshCallsBeforeForce));
                    await WaitUntilAsync(() => Task.FromResult(
                        cosmosFactory.Services.GetRequiredService<IChannelRefreshQueue>().Count == 0));

                    using var createShareResponse = await cosmosClient.PostAsync(
                        importedListPath + "/share/create",
                        EmptyForm());
                    Assert.Equal(HttpStatusCode.Redirect, createShareResponse.StatusCode);
                    var shareRepository = cosmosFactory.Services.GetRequiredService<IShareLinkRepository>();
                    var createdShare = Assert.Single(await shareRepository.GetByListAsync(listId));
                    using var shareListResponse = await cosmosClient.GetAsync(importedListPath + "/share");
                    Assert.Equal(HttpStatusCode.OK, shareListResponse.StatusCode);
                    Assert.Contains("Active", await shareListResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
                    using var consumeResponse = await cosmosClient.GetAsync($"/share/{createdShare.Password}");
                    Assert.Equal(HttpStatusCode.Redirect, consumeResponse.StatusCode);
                    Assert.Equal(importedListPath, consumeResponse.Headers.Location?.OriginalString);
                    using var rejectedReuse = await cosmosClient.GetAsync($"/share/{createdShare.Password}");
                    Assert.NotEqual(importedListPath, rejectedReuse.Headers.Location?.OriginalString);
                    using var deleteShareResponse = await cosmosClient.PostAsync(
                        importedListPath + "/share/delete",
                        Form(("password", createdShare.Password)));
                    Assert.Equal(HttpStatusCode.Redirect, deleteShareResponse.StatusCode);
                    Assert.Empty(await shareRepository.GetByListAsync(listId));

                    using var createListResponse = await cosmosClient.PostAsync(
                        "/create-list",
                        Form(("Title", "Disposable smoke list")));
                    Assert.Equal(HttpStatusCode.Redirect, createListResponse.StatusCode);
                    var disposablePath = createListResponse.Headers.Location?.OriginalString;
                    Assert.NotNull(disposablePath);
                    var disposableId = Guid.Parse(
                        disposablePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[2]);
                    using var deleteListResponse = await cosmosClient.PostAsync(
                        disposablePath + "/delete",
                        Form(("Confirm", "true")));
                    Assert.Equal(HttpStatusCode.Redirect, deleteListResponse.StatusCode);
                    Assert.Null(await hostLists.GetAsync(disposableId));
                    using var deletedListResponse = await cosmosClient.GetAsync(disposablePath);
                    Assert.NotEqual(HttpStatusCode.OK, deletedListResponse.StatusCode);
                }
                smokeStopwatch.Stop();
                downtimeStopwatch.Stop();

                var sourceAfterSmoke = await secondRehearsal.RunAsync(
                    new SqlToCosmosImportOptions(
                        SqlToCosmosImportMode.Validate,
                        2,
                        ConfirmEmptyTarget: false,
                        ConfirmPreCutoverRerun: false),
                    migrationAt,
                    CancellationToken.None);
                Assert.Equal(validation.ReconciliationHash, sourceAfterSmoke.ReconciliationHash);

                var mismatched = await secondTarget.Context.Lists.ReadItemAsync<CosmosListDocument>(
                    listId.ToString("D"),
                    new Microsoft.Azure.Cosmos.PartitionKey(listId.ToString("D")));
                mismatched.Resource.Title = "Injected reconciliation failure";
                await secondTarget.Context.Lists.ReplaceItemAsync(
                    mismatched.Resource,
                    mismatched.Resource.Id,
                    new Microsoft.Azure.Cosmos.PartitionKey(mismatched.Resource.Id));
                var failedReconciliation = new SqlToCosmosImportService(
                    new SqlImportSource(source.ConnectionString),
                    secondImportTarget,
                    output,
                    migrationClock);
                var reconciliationFailure = await Assert.ThrowsAsync<SqlToCosmosImportOperationException>(
                    () => failedReconciliation.RunAsync(
                        new SqlToCosmosImportOptions(
                            SqlToCosmosImportMode.Reconcile,
                            2,
                            ConfirmEmptyTarget: false,
                            ConfirmPreCutoverRerun: false),
                        migrationAt,
                        CancellationToken.None));
                Assert.Equal(SqlToCosmosImportError.ReconciliationListMismatch, reconciliationFailure.Error);
                Assert.Equal(
                    "Representative list",
                    (await new ListRepository(
                        new ConnectionStringConnectionFactory(source.ConnectionString))
                        .GetAsync(listId)).Title);

                var reconciliationRollbackStopwatch = Stopwatch.StartNew();
                using (var reconciliationRollbackFactory = RehearsalWebApplicationFactory.ForSql(
                    source.ConnectionString,
                    migrationClock,
                    shareCreationEnabled: false))
                using (var reconciliationRollbackClient = reconciliationRollbackFactory.CreateClient(
                    new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }))
                {
                    Assert.IsType<ListRepository>(
                        reconciliationRollbackFactory.Services.GetRequiredService<IListRepository>());
                    using var reconciliationRollbackSmoke = await reconciliationRollbackClient.GetAsync(
                        importedListPath);
                    Assert.Equal(HttpStatusCode.OK, reconciliationRollbackSmoke.StatusCode);
                    Assert.Contains(
                        "Representative list",
                        await reconciliationRollbackSmoke.Content.ReadAsStringAsync(),
                        StringComparison.Ordinal);
                }
                reconciliationRollbackStopwatch.Stop();

                var smokeRollbackStopwatch = Stopwatch.StartNew();
                using (var failingCosmosFactory = RehearsalWebApplicationFactory.ForCosmos(
                    CosmosEmulatorOptions.FromEnvironment().ConnectionString,
                    target.DatabaseName,
                    migrationClock,
                    youtube,
                    injectSmokeFailure: true))
                using (var failingCosmosClient = failingCosmosFactory.CreateClient(
                    new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }))
                {
                    Assert.IsType<CosmosListRepository>(
                        failingCosmosFactory.Services.GetRequiredService<IListRepository>());
                    using var failedSmoke = await failingCosmosClient.GetAsync(importedListPath);
                    Assert.NotEqual(HttpStatusCode.OK, failedSmoke.StatusCode);
                    Assert.NotEqual(importedListPath, failedSmoke.Headers.Location?.OriginalString);
                }

                using (var smokeRollbackFactory = RehearsalWebApplicationFactory.ForSql(
                    source.ConnectionString,
                    migrationClock,
                    shareCreationEnabled: false))
                using (var smokeRollbackClient = smokeRollbackFactory.CreateClient(
                    new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }))
                {
                    Assert.IsType<ListRepository>(
                        smokeRollbackFactory.Services.GetRequiredService<IListRepository>());
                    using var smokeRollbackSmoke = await smokeRollbackClient.GetAsync(importedListPath);
                    Assert.Equal(HttpStatusCode.OK, smokeRollbackSmoke.StatusCode);
                    Assert.Contains(
                        "Representative list",
                        await smokeRollbackSmoke.Content.ReadAsStringAsync(),
                        StringComparison.Ordinal);
                }
                smokeRollbackStopwatch.Stop();

                var smokeRecords = cosmosFactory.CosmosRecords;
                var smokeRu = smokeRecords.Sum(record => record.RequestCharge);
                Assert.True(smokeRu > 0);
                Assert.DoesNotContain(
                    smokeRecords,
                    record => record.Status == 429);
                _output.WriteLine(
                    $"StoppedSite ShareCreationBlocked=true DrainMinutes=76 ValidLinksAfterDrain=0 " +
                    $"WritesStoppedAtUtc={writesStoppedAtUtc:O} DowntimeMs={downtimeStopwatch.Elapsed.TotalMilliseconds:0.##} SourceUnchanged=true");
                _output.WriteLine(
                    $"MigrationRehearsal Rehearsal=1 InterruptedReruns=3 " +
                    $"DurationMs={firstRehearsalStopwatch.Elapsed.TotalMilliseconds:0.##} " +
                    $"TargetSdkOperations={firstRehearsalMetrics.RequestCount} " +
                    $"TargetOperationRu={firstRehearsalMetrics.RequestCharge:0.##} " +
                    $"SurfacedThrottles={firstRehearsalMetrics.SurfacedThrottleCount} " +
                    $"Hash={firstReconciliation.ReconciliationHash}");
                _output.WriteLine(
                    $"MigrationRehearsal Rehearsal=2 InterruptedReruns=0 " +
                    $"DurationMs={secondRehearsalStopwatch.Elapsed.TotalMilliseconds:0.##} " +
                    $"TargetSdkOperations={secondRehearsalMetrics.RequestCount} " +
                    $"TargetOperationRu={secondRehearsalMetrics.RequestCharge:0.##} " +
                    $"SurfacedThrottles={secondRehearsalMetrics.SurfacedThrottleCount} " +
                    $"Hash={secondReconciliation.ReconciliationHash}");
                _output.WriteLine(
                    $"PreOpenSmoke Provider=Cosmos CompletedRefresh=true AddRemoveReadd=true ForceRefresh=true " +
                    $"ShareFlow=true ListDelete=true DurationMs={smokeStopwatch.Elapsed.TotalMilliseconds:0.##} " +
                    $"Requests={smokeRecords.Sum(record => record.RequestCount)} Ru={smokeRu:0.##} Throttles=0");
                _output.WriteLine(
                    $"FailureInjection Type=Smoke Result=FailedAsInjected RollbackProvider=SqlServer " +
                    $"RollbackPassed=true DurationMs={smokeRollbackStopwatch.Elapsed.TotalMilliseconds:0.##}");
                _output.WriteLine(
                    $"FailureInjection Type=Reconciliation Result=ListMismatch RollbackProvider=SqlServer " +
                    $"RollbackPassed=true SourceUnchanged=true " +
                    $"DurationMs={reconciliationRollbackStopwatch.Elapsed.TotalMilliseconds:0.##}");

                var safeOutput = output.ToString();
                Assert.DoesNotContain(Convert.ToBase64String(token), safeOutput, StringComparison.Ordinal);
                Assert.DoesNotContain("Representative list", safeOutput, StringComparison.Ordinal);
                Assert.DoesNotContain("share-password", safeOutput, StringComparison.Ordinal);
                Assert.Contains("Lists=1 Channels=2", safeOutput, StringComparison.Ordinal);
            }
            finally
            {
                await secondTarget.DisposeAsync();
                await target.DisposeAsync();
                await source.DisposeAsync();
            }
        }

        private static async Task SeedSourceAsync(
            LocalDbTestFixture source,
            DateTimeOffset importedAt,
            Guid listId,
            Guid expiredListId,
            byte[] token)
        {
            await using var connection = source.CreateConnection();
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                @"INSERT INTO Channel
                      (Id, Url, Title, Thumbnail, PlaylistId, StaleAfter, Status, StatusReason, StatusUpdatedAt)
                  VALUES
                      ('channel-active', N'https://example.test/active', N'Active channel', N'https://example.test/active.jpg', 'playlist-active', @activeStale, 0, 0, NULL),
                      ('channel-unavailable', N'https://example.test/unavailable', N'Unavailable channel', N'https://example.test/unavailable.jpg', 'playlist-unavailable', @unavailableStale, 1, 1, @statusUpdatedAt),
                      ('expired-only-channel', N'https://example.test/expired', N'Expired channel', N'https://example.test/expired.jpg', 'playlist-expired', @activeStale, 0, 0, NULL),
                      ('unreferenced-channel', N'https://example.test/unreferenced', N'Unreferenced channel', N'https://example.test/unreferenced.jpg', 'playlist-unreferenced', @activeStale, 0, 0, NULL);

                  INSERT INTO [List]
                      (Id, Token, Title, PlaybackRate, ExpiredAfter, ExpirationRenewedOn)
                  VALUES
                      (@listId, @token, N'Representative list', 1.50, @expiresAfter, @renewedOn),
                      (@expiredListId, @expiredToken, N'Expired list', 1.00, @importedAt, NULL);

                  INSERT INTO ListChannel (ListId, ChannelId)
                  VALUES
                      (@listId, 'channel-unavailable'),
                      (@listId, 'channel-active'),
                      (@expiredListId, 'expired-only-channel');

                  INSERT INTO ShareLink (Password, ListId, CreatedAt, ExpiresAfter, UsedAt)
                  VALUES (N'share-password', @listId, @shareCreatedAt, @shareExpiresAfter, NULL);

                  CREATE TABLE WorkerState (Id INT NOT NULL PRIMARY KEY, NextRunAt DATETIMEOFFSET NULL);
                  INSERT INTO WorkerState (Id, NextRunAt) VALUES (1, @createdAt);",
                new
                {
                    listId,
                    expiredListId,
                    token,
                    expiredToken = Enumerable.Repeat((byte)99, 40).ToArray(),
                    importedAt,
                    expiresAfter = importedAt.AddDays(2),
                    renewedOn = DateOnly.FromDateTime(importedAt.UtcDateTime).AddDays(-1),
                    createdAt = importedAt.AddHours(-4),
                    shareCreatedAt = importedAt,
                    shareExpiresAfter = importedAt.Add(Constants.ShareLinkMaxAgeMax),
                    activeStale = importedAt.AddHours(-1),
                    unavailableStale = importedAt.AddHours(-2),
                    statusUpdatedAt = importedAt.AddHours(-3)
                });

            var videos = Enumerable.Range(0, 101).Select(value => new
            {
                ChannelId = "channel-active",
                Id = $"video-{value:D3}",
                Title = $"Video {value}",
                Duration = TimeSpan.FromMinutes(value + 1).Ticks,
                PublishedAt = importedAt.AddMinutes(value),
                Thumbnail = $"https://example.test/video-{value:D3}.jpg"
            });
            await connection.ExecuteAsync(
                @"INSERT INTO ChannelVideo (ChannelId, Id, Title, Duration, PublishedAt, Thumbnail)
                  VALUES (@ChannelId, @Id, @Title, @Duration, @PublishedAt, @Thumbnail);",
                videos);
        }

        private static async Task ClearTargetAsync(CosmosTestFixture target, Guid listId)
        {
            await target.Context.Lists.DeleteItemAsync<CosmosListDocument>(
                listId.ToString("D"),
                new Microsoft.Azure.Cosmos.PartitionKey(listId.ToString("D")));
            foreach (var channelId in new[] { "channel-active", "channel-unavailable" })
            {
                await target.Context.Channels.DeleteItemAsync<CosmosChannelDocument>(
                    channelId,
                    new Microsoft.Azure.Cosmos.PartitionKey(channelId));
            }
        }

        private static async Task<IReadOnlyList<T>> ReadAllAsync<T>(IAsyncEnumerable<T> values)
        {
            var result = new List<T>();
            await foreach (var value in values)
            {
                result.Add(value);
            }
            return result;
        }

        private static async Task<long> CountValidUnconsumedShareLinksAsync(
            LocalDbTestFixture source,
            DateTimeOffset now)
        {
            await using var connection = source.CreateConnection();
            await connection.OpenAsync();
            return await connection.ExecuteScalarAsync<long>(
                @"SELECT COUNT_BIG(*)
                  FROM ShareLink
                  WHERE UsedAt IS NULL AND ExpiresAfter > @now;",
                new { now });
        }

        private static FormUrlEncodedContent EmptyForm()
        {
            return new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>());
        }

        private static FormUrlEncodedContent Form(params (string Key, string Value)[] values)
        {
            return new FormUrlEncodedContent(values.Select(value =>
                new KeyValuePair<string, string>(value.Key, value.Value)));
        }

        private static async Task WaitUntilAsync(Func<Task<bool>> predicate)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (await predicate())
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }

            Assert.Fail("The rehearsal condition did not complete before its deadline.");
        }

        private sealed class RehearsalWebApplicationFactory : WebApplicationFactory<global::Program>
        {
            private readonly FakeAppClock _clock;
            private readonly FakeYoutubeService _youtube;
            private readonly bool _injectSmokeFailure;
            private readonly CosmosRequestRecorder<CosmosListRepository> _listRecorder = new();
            private readonly CosmosRequestRecorder<CosmosChannelRepository> _channelRecorder = new();
            private readonly CosmosRequestRecorder<CosmosShareLinkRepository> _shareRecorder = new();
            private readonly Dictionary<string, string> _originalEnvironment = new();
            private bool _environmentRestored;

            private RehearsalWebApplicationFactory(
                IReadOnlyDictionary<string, string> settings,
                FakeAppClock clock,
                FakeYoutubeService youtube,
                bool injectSmokeFailure)
            {
                _clock = clock;
                _youtube = youtube ?? new FakeYoutubeService();
                _injectSmokeFailure = injectSmokeFailure;
                SetEnvironment("ASPNETCORE_ENVIRONMENT", Environments.Production);
                SetEnvironment("DOTNET_ENVIRONMENT", Environments.Production);
                foreach (var setting in settings)
                {
                    SetEnvironment(
                        setting.Key.Replace(":", "__", StringComparison.Ordinal),
                        setting.Value);
                }
            }

            public IReadOnlyList<CosmosRequestRecord> CosmosRecords => _listRecorder.Records
                .Concat(_channelRecorder.Records)
                .Concat(_shareRecorder.Records)
                .ToArray();

            public static RehearsalWebApplicationFactory ForSql(
                string connectionString,
                FakeAppClock clock,
                bool shareCreationEnabled)
            {
                return new RehearsalWebApplicationFactory(
                    new Dictionary<string, string>
                    {
                        ["Persistence:Provider"] = PersistenceProvider.SqlServer.ToString(),
                        ["ConnectionStrings:Main"] = connectionString,
                        ["ShareLinks:CreationEnabled"] = shareCreationEnabled.ToString()
                    },
                    clock,
                    youtube: null,
                    injectSmokeFailure: false);
            }

            public static RehearsalWebApplicationFactory ForCosmos(
                string connectionString,
                string databaseName,
                FakeAppClock clock,
                FakeYoutubeService youtube,
                bool injectSmokeFailure = false)
            {
                return new RehearsalWebApplicationFactory(
                    new Dictionary<string, string>
                    {
                        ["Persistence:Provider"] = PersistenceProvider.Cosmos.ToString(),
                        ["Cosmos:ConnectionString"] = connectionString,
                        ["Cosmos:DatabaseName"] = databaseName,
                        ["ShareLinks:CreationEnabled"] = bool.TrueString
                    },
                    clock,
                    youtube,
                    injectSmokeFailure);
            }

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseEnvironment(Environments.Production);
                builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Critical));
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IAppClock>();
                    services.RemoveAll<IYoutubeService>();
                    services.RemoveAll<IYoutubeCallDelay>();
                    foreach (var maintenance in services
                        .Where(service => service.ServiceType == typeof(IHostedService)
                            && service.ImplementationType == typeof(MaintenanceHostedService))
                        .ToArray())
                    {
                        services.Remove(maintenance);
                    }

                    services.PostConfigure<MvcOptions>(options =>
                    {
                        foreach (var filter in options.Filters
                            .OfType<AutoValidateAntiforgeryTokenAttribute>()
                            .ToArray())
                        {
                            options.Filters.Remove(filter);
                        }
                    });
                    services.AddSingleton<IAppClock>(_clock);
                    services.AddSingleton<IYoutubeService>(_youtube);
                    services.AddSingleton<IYoutubeCallDelay, ImmediateYoutubeCallDelay>();
                    services.AddSingleton<ILogger<CosmosListRepository>>(_listRecorder);
                    services.AddSingleton<ILogger<CosmosChannelRepository>>(_channelRecorder);
                    services.AddSingleton<ILogger<CosmosShareLinkRepository>>(_shareRecorder);

                    if (_injectSmokeFailure)
                    {
                        services.RemoveAll<IListService>();
                        services.AddSingleton<IListService>(provider =>
                            new InjectedSmokeFailureListService(new ListService(
                                provider.GetRequiredService<IListRepository>(),
                                provider.GetRequiredService<IAppClock>(),
                                provider.GetRequiredService<IChannelRefreshQueue>())));
                    }
                });
            }

            protected override IHost CreateHost(IHostBuilder builder)
            {
                try
                {
                    return base.CreateHost(builder);
                }
                finally
                {
                    RestoreEnvironment();
                }
            }

            protected override void Dispose(bool disposing)
            {
                RestoreEnvironment();
                base.Dispose(disposing);
            }

            private void SetEnvironment(string name, string value)
            {
                _originalEnvironment[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
            }

            private void RestoreEnvironment()
            {
                if (_environmentRestored)
                {
                    return;
                }

                foreach (var value in _originalEnvironment)
                {
                    Environment.SetEnvironmentVariable(value.Key, value.Value);
                }

                _environmentRestored = true;
            }
        }

        private sealed class InjectedSmokeFailureListService : IListService
        {
            private readonly IListService _inner;

            public InjectedSmokeFailureListService(IListService inner)
            {
                _inner = inner;
            }

            public Task<ListModel> CreateListAsync(string title) => _inner.CreateListAsync(title);
            public Task<ListModel> GetListAsync(Guid id) => _inner.GetListAsync(id);
            public Task<ListModel> GetAuthenticatedListAsync(Guid id, string token) =>
                _inner.GetAuthenticatedListAsync(id, token);
            public Task<ListViewModel> GetAuthenticatedListViewAsync(Guid id, string token) =>
                Task.FromResult<ListViewModel>(null);
            public Task<ListViewModel> GetListViewAsync(Guid id) => _inner.GetListViewAsync(id);
            public Task<ListViewModel> GetListViewAsync(ListModel list) => _inner.GetListViewAsync(list);
            public Task<ListViewModel> GetListChannelViewAsync(Guid id) => _inner.GetListChannelViewAsync(id);
            public Task<ListViewModel> GetListChannelViewAsync(ListModel list) =>
                _inner.GetListChannelViewAsync(list);
            public Task ForceRefreshAsync(ListModel list) => _inner.ForceRefreshAsync(list);
            public Task AddChannelAsync(Guid listId, string channelId) =>
                _inner.AddChannelAsync(listId, channelId);
            public Task RemoveChannelAsync(Guid listId, string channelId) =>
                _inner.RemoveChannelAsync(listId, channelId);
            public Task UpdateListAsync(Guid id, string title, decimal playbackRate) =>
                _inner.UpdateListAsync(id, title, playbackRate);
            public Task DeleteListAsync(Guid id) => _inner.DeleteListAsync(id);
        }

        private sealed class ImmediateYoutubeCallDelay : IYoutubeCallDelay
        {
            public Task DelayAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }

        private sealed class InterruptAfterDurableWriteTarget : ISqlToCosmosImportTarget
        {
            private readonly ISqlToCosmosImportTarget _inner;
            private readonly int _interruptionPoint;
            private int _writes;
            private bool _hasInterrupted;

            public InterruptAfterDurableWriteTarget(
                ISqlToCosmosImportTarget inner,
                int interruptionPoint)
            {
                _inner = inner;
                _interruptionPoint = interruptionPoint;
            }

            public IAsyncEnumerable<CosmosListDocument> ReadListsAsync(
                int batchSize,
                CancellationToken cancellationToken) => _inner.ReadListsAsync(batchSize, cancellationToken);

            public IAsyncEnumerable<CosmosChannelDocument> ReadChannelsAsync(
                int batchSize,
                CancellationToken cancellationToken) => _inner.ReadChannelsAsync(batchSize, cancellationToken);

            public Task<int> CountShareLinksAsync(CancellationToken cancellationToken)
                => _inner.CountShareLinksAsync(cancellationToken);

            public Task UpsertListAsync(CosmosListDocument document, CancellationToken cancellationToken)
                => UpsertAndInterruptAsync(
                    () => _inner.UpsertListAsync(document, cancellationToken));

            public async Task UpsertChannelAsync(
                CosmosChannelDocument document,
                CancellationToken cancellationToken)
            {
                await UpsertAndInterruptAsync(
                    () => _inner.UpsertChannelAsync(document, cancellationToken));
            }

            private async Task UpsertAndInterruptAsync(Func<Task> upsert)
            {
                await upsert();
                _writes++;
                if (!_hasInterrupted && _writes == _interruptionPoint)
                {
                    _hasInterrupted = true;
                    throw new SimulatedInterruptionException();
                }
            }
        }

        private sealed class SimulatedInterruptionException : Exception
        {
        }
    }
}
