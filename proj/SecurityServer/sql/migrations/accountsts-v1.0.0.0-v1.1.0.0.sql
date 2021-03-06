sp_rename AppMember, IdentityUser;
GO
sp_rename IdentityService, IdentityServiceUser;
GO
sp_rename UserModel, HmacUser;
GO
sp_rename AppModel, HmacConsumer;
GO
DROP TABLE IdentityUserClaim;
GO
CREATE TABLE [IdentityConsumerUser] (
  [UserId] nvarchar(50) NOT NULL,
  [AppId] nvarchar(50) NOT NULL
);
GO
ALTER TABLE [IdentityConsumerUser] ADD CONSTRAINT [PK_IdentityConsumerUser] PRIMARY KEY ([UserId],[AppId]);
GO
CREATE TABLE [UserActivity] (
  [UserId] nvarchar(50) NOT NULL,
  [AppId] nvarchar(50) NOT NULL,
  [ServiceId] nvarchar(50) NOT NULL,
  [DateTime] datetime NOT NULL,
  [Type] nvarchar(50) NOT NULL,
  [Details] nvarchar(200) NULL
);
GO
CREATE TABLE [ConsumerInfo] (
  [AppId] nvarchar(50) PRIMARY KEY,
  [GroupId] nvarchar(50) NULL,
  [Url] nvarchar(200) NOT NULL,
  [Name] nvarchar(100) NULL
  );
GO
