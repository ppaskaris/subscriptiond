IF OBJECT_ID('ListChannel', 'U') IS NOT NULL
	DROP TABLE ListChannel;

CREATE TABLE ListChannel (
	ListId UNIQUEIDENTIFIER NOT NULL,
	ChannelId NVARCHAR (50) NOT NULL,

	CONSTRAINT PK_ListChannel PRIMARY KEY (ListId, ChannelId),
	CONSTRAINT FK_ListChannel_ChannelId FOREIGN KEY (ChannelId) REFERENCES Channel (Id)
);
