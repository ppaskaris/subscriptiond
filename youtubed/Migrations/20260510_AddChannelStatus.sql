SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'youtubed.ChannelStatus', N'U') IS NULL
BEGIN
    CREATE TABLE youtubed.ChannelStatus (
        Id INT NOT NULL,
        Name NVARCHAR(50) NOT NULL,

        CONSTRAINT PK_ChannelStatus PRIMARY KEY (Id),
        CONSTRAINT UK_ChannelStatus_Name UNIQUE (Name)
    );
END;

MERGE INTO youtubed.ChannelStatus target
USING (
    VALUES
        (0, N'Active'),
        (1, N'Unavailable')
) source (Id, Name)
    ON source.Id = target.Id
WHEN MATCHED AND target.Name <> source.Name THEN
    UPDATE SET Name = source.Name
WHEN NOT MATCHED THEN
    INSERT (Id, Name)
    VALUES (source.Id, source.Name);

IF OBJECT_ID(N'youtubed.ChannelStatusReason', N'U') IS NULL
BEGIN
    CREATE TABLE youtubed.ChannelStatusReason (
        Id INT NOT NULL,
        Name NVARCHAR(100) NOT NULL,

        CONSTRAINT PK_ChannelStatusReason PRIMARY KEY (Id),
        CONSTRAINT UK_ChannelStatusReason_Name UNIQUE (Name)
    );
END;

MERGE INTO youtubed.ChannelStatusReason target
USING (
    VALUES
        (0, N'None'),
        (1, N'NotFound'),
        (2, N'Deleted'),
        (3, N'Private'),
        (4, N'PlaylistUnavailable')
) source (Id, Name)
    ON source.Id = target.Id
WHEN MATCHED AND target.Name <> source.Name THEN
    UPDATE SET Name = source.Name
WHEN NOT MATCHED THEN
    INSERT (Id, Name)
    VALUES (source.Id, source.Name);

IF COL_LENGTH(N'youtubed.Channel', N'Status') IS NULL
BEGIN
    ALTER TABLE youtubed.Channel
        ADD Status INT NULL;
END;

IF COL_LENGTH(N'youtubed.Channel', N'StatusReason') IS NULL
BEGIN
    ALTER TABLE youtubed.Channel
        ADD StatusReason INT NULL;
END;

IF COL_LENGTH(N'youtubed.Channel', N'StatusUpdatedAt') IS NULL
BEGIN
    ALTER TABLE youtubed.Channel
        ADD StatusUpdatedAt DATETIMEOFFSET NULL;
END;

EXEC sp_executesql N'
    UPDATE youtubed.Channel
    SET Status = 0
    WHERE Status IS NULL;
';

EXEC sp_executesql N'
    UPDATE youtubed.Channel
    SET StatusReason = 0
    WHERE StatusReason IS NULL;
';

EXEC sp_executesql N'
    ALTER TABLE youtubed.Channel
        ALTER COLUMN Status INT NOT NULL;
';

EXEC sp_executesql N'
    ALTER TABLE youtubed.Channel
        ALTER COLUMN StatusReason INT NOT NULL;
';

IF OBJECT_ID(N'youtubed.DF_Channel_Status', N'D') IS NULL
BEGIN
    ALTER TABLE youtubed.Channel
        ADD CONSTRAINT DF_Channel_Status DEFAULT (0) FOR Status;
END;

IF OBJECT_ID(N'youtubed.DF_Channel_StatusReason', N'D') IS NULL
BEGIN
    ALTER TABLE youtubed.Channel
        ADD CONSTRAINT DF_Channel_StatusReason DEFAULT (0) FOR StatusReason;
END;

IF OBJECT_ID(N'youtubed.FK_Channel_ChannelStatus', N'F') IS NULL
BEGIN
    ALTER TABLE youtubed.Channel
        ADD CONSTRAINT FK_Channel_ChannelStatus FOREIGN KEY (Status) REFERENCES youtubed.ChannelStatus (Id);
END;

IF OBJECT_ID(N'youtubed.FK_Channel_ChannelStatusReason', N'F') IS NULL
BEGIN
    ALTER TABLE youtubed.Channel
        ADD CONSTRAINT FK_Channel_ChannelStatusReason FOREIGN KEY (StatusReason) REFERENCES youtubed.ChannelStatusReason (Id);
END;

COMMIT TRANSACTION;
