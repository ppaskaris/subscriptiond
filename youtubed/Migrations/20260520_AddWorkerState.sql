SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'youtubed.WorkerState', N'U') IS NULL
BEGIN
    CREATE TABLE youtubed.WorkerState (
        Id INT NOT NULL,
        NextChannelRefreshAt DATETIMEOFFSET NULL,
        NextPurgeAt DATETIMEOFFSET NOT NULL,

        CONSTRAINT PK_WorkerState PRIMARY KEY (Id),
        CONSTRAINT CK_WorkerState_Id CHECK (Id = 1)
    );
END;

COMMIT TRANSACTION;
