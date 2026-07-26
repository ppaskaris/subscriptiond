IF COL_LENGTH(N'dbo.WorkerState', N'NextConsistencyRecoveryAt') IS NULL
BEGIN
    ALTER TABLE dbo.WorkerState
        ADD NextConsistencyRecoveryAt DATETIMEOFFSET NULL;
END;

IF COL_LENGTH(N'dbo.WorkerState', N'ConsistencyRecoveryForceCount') IS NULL
BEGIN
    ALTER TABLE dbo.WorkerState
        ADD ConsistencyRecoveryForceCount BIGINT NOT NULL
            CONSTRAINT DF_WorkerState_ConsistencyRecoveryForceCount DEFAULT (0);
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.default_constraints dc
    INNER JOIN sys.columns c
        ON c.object_id = dc.parent_object_id
        AND c.column_id = dc.parent_column_id
    WHERE dc.parent_object_id = OBJECT_ID(N'dbo.WorkerState')
      AND c.name = N'ConsistencyRecoveryForceCount'
)
BEGIN
    IF OBJECT_ID(N'dbo.DF_WorkerState_ConsistencyRecoveryForceCount', N'D') IS NOT NULL
        THROW 50001, 'DF_WorkerState_ConsistencyRecoveryForceCount exists on an unexpected column.', 1;

    ALTER TABLE dbo.WorkerState
        ADD CONSTRAINT DF_WorkerState_ConsistencyRecoveryForceCount
        DEFAULT (0) FOR ConsistencyRecoveryForceCount;
END;

EXEC sys.sp_executesql N'
    UPDATE dbo.WorkerState
    SET NextConsistencyRecoveryAt = COALESCE(NextConsistencyRecoveryAt, SYSUTCDATETIME())
    WHERE NextConsistencyRecoveryAt IS NULL;

    ALTER TABLE dbo.WorkerState
        ALTER COLUMN NextConsistencyRecoveryAt DATETIMEOFFSET NOT NULL;
';
