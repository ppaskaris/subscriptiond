using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;
using youtubed.Persistence;

namespace youtubed.Tests.Persistence
{
    public sealed class PersistenceBoundaryArchitectureTests
    {
        [Fact]
        public void PersistencePorts_DoNotReferenceMvcOrProviderDtoTypes()
        {
            var assembly = typeof(IListRepository).Assembly;
            var ports = assembly.GetTypes()
                .Where(type => type.IsInterface)
                .Where(type => type.Namespace == typeof(IListRepository).Namespace)
                .ToArray();

            Assert.NotEmpty(ports);
            foreach (var port in ports)
            {
                var referencedTypes = port.GetMethods()
                    .SelectMany(method => method.GetParameters()
                        .Select(parameter => parameter.ParameterType)
                        .Append(method.ReturnType))
                    .SelectMany(GetTypeClosure);

                Assert.DoesNotContain(referencedTypes, IsForbiddenBoundaryType);
            }
        }

        [Fact]
        public void CosmosProviderImplementation_IsTemporarilyAbsent()
        {
            var assembly = typeof(IListRepository).Assembly;
            var cosmosTypes = assembly.GetTypes()
                .Where(type => type.Namespace == "youtubed.Persistence.Cosmos")
                .ToArray();

            Assert.Empty(cosmosTypes);
        }

        [Fact]
        public void ObsoleteChannelVideoPortAndSqlRow_AreRemoved()
        {
            var assembly = typeof(IListRepository).Assembly;

            Assert.Null(assembly.GetType("youtubed.Persistence.IChannelVideoRepository"));
            Assert.Null(assembly.GetType("youtubed.Persistence.ChannelVideoRecord"));
        }

        private static IEnumerable<Type> GetTypeClosure(Type type)
        {
            yield return type;

            if (type.HasElementType)
            {
                foreach (var elementType in GetTypeClosure(type.GetElementType()))
                {
                    yield return elementType;
                }
            }

            foreach (var argument in type.GetGenericArguments())
            {
                foreach (var argumentType in GetTypeClosure(argument))
                {
                    yield return argumentType;
                }
            }
        }

        private static bool IsForbiddenBoundaryType(Type type)
        {
            return type.Namespace == "youtubed.Models"
                || type.Namespace == "youtubed.Persistence.Cosmos"
                || type.Name.EndsWith("Row", StringComparison.Ordinal)
                || type.Name.EndsWith("Document", StringComparison.Ordinal);
        }
    }
}
