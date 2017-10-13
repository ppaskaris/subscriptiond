﻿IF OBJECT_ID('ChannelVideo', 'U') IS NOT NULL
	DROP TABLE ChannelVideo;

CREATE TABLE ChannelVideo (
	ChannelId NVARCHAR (50) NOT NULL,
	Id NVARCHAR (50) NOT NULL,
	Title NVARCHAR (100) NOT NULL,
	Duration BIGINT NOT NULL,
	PublishedAt DATETIMEOFFSET NOT NULL,
	Thumbnail NVARCHAR(2000) NULL,

	CONSTRAINT PK_ChannelVideo PRIMARY KEY (ChannelId, Id),
	CONSTRAINT FK_ChannelVideo_ChannelId FOREIGN KEY (ChannelId) REFERENCES Channel (Id)
);