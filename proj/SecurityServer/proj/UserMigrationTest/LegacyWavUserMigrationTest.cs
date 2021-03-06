﻿using System.Configuration;
using System.Threading.Tasks;
using Dragon.Data.Repositories;
using Dragon.SecurityServer.AccountSTS.Models;
using Dragon.SecurityServer.Identity.Models;
using Dragon.SecurityServer.Identity.Stores;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UserMigration;

namespace UserMigrationTest
{
    [TestClass]
    public class LegacyWavUserMigrationTest
    {
        private const string LegacyPasswordPrefix = "OLD_";

        [TestMethod]
        public async Task Migrate_validDbs_shouldMigrateUsers()
        {
            ConnectionHelper.ConnectionString = () => ConfigurationManager.ConnectionStrings["AccountSts"].ConnectionString;
            var userStore = new UserStore<AppMember>(new Repository<AppMember>(), new Repository<IdentityUserClaim>(), new Repository<IdentityUserLogin>(), new Repository<IdentityUserService>(), new Repository<IdentityUserApp>());
            var service = new LegacyWavUserMigration<AppMember>(userStore);
            await service.Migrate(data => new AppMember
            {
                Id = data.UserID.ToString(),
                PasswordHash = string.IsNullOrWhiteSpace(data.Secret) ? "" : LegacyPasswordPrefix + data.Secret,
                UserName = data.Email,
                Email = data.Email,
                EmailConfirmed = false, // currently no email confirmation is used
                Created = data.Created,
                Modified = data.Modified
            });
        }
    }
}
