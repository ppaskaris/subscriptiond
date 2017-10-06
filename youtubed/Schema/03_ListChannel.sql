﻿IF OBJECT_ID('ListChannel', 'U') IS NOT NULL 
	DROP TABLE ListChannel; 

CREATE TABLE ListChannel (
	ListId UNIQUEIDENTIFIER NOT NULL,
	ChannelId NVARCHAR (50) NOT NULL,

	PRIMARY KEY (ListId, ChannelId)
);
