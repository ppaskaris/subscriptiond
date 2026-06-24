SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'youtubed.WorkerState', N'U') IS NOT NULL
   AND COL_LENGTH(N'youtubed.WorkerState', N'ChannelRefreshForceCount') IS NULL
BEGIN
    ALTER TABLE youtubed.WorkerState
    ADD ChannelRefreshForceCount BIGINT NOT NULL
        CONSTRAINT DF_WorkerState_ChannelRefreshForceCount DEFAULT (0);
END;

COMMIT TRANSACTION;
