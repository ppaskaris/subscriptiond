SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH(N'youtubed.List', N'PlaybackRate') IS NULL
BEGIN
    ALTER TABLE youtubed.[List]
        ADD PlaybackRate DECIMAL(3, 2) NULL;
END;

EXEC sp_executesql N'
    UPDATE youtubed.[List]
    SET PlaybackRate = 2.00
    WHERE PlaybackRate IS NULL;
';

DECLARE @defaultConstraintName SYSNAME;

SELECT @defaultConstraintName = default_constraints.name
FROM sys.default_constraints
INNER JOIN sys.columns
    ON columns.default_object_id = default_constraints.object_id
WHERE default_constraints.parent_object_id = OBJECT_ID(N'youtubed.List')
  AND columns.name = N'PlaybackRate';

IF @defaultConstraintName IS NOT NULL
BEGIN
    DECLARE @dropDefaultSql NVARCHAR(MAX) =
        N'ALTER TABLE youtubed.[List] DROP CONSTRAINT ' + QUOTENAME(@defaultConstraintName) + N';';
    EXEC sp_executesql @dropDefaultSql;
END;

EXEC sp_executesql N'
    ALTER TABLE youtubed.[List]
        ALTER COLUMN PlaybackRate DECIMAL(3, 2) NOT NULL;
';

EXEC sp_executesql N'
    ALTER TABLE youtubed.[List]
        ADD CONSTRAINT DF_List_PlaybackRate DEFAULT (1.00) FOR PlaybackRate;
';

COMMIT TRANSACTION;
