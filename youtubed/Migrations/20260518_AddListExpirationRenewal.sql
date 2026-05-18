SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH(N'youtubed.List', N'ExpirationRenewedOn') IS NULL
BEGIN
    ALTER TABLE youtubed.[List]
        ADD ExpirationRenewedOn DATE NULL;
END;

COMMIT TRANSACTION;
