using ASQATools.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Moq;
using Xunit;

namespace ASQATools.Tests.Unit;

public class RbacSecurityServiceTests
{
    [Fact]
    public void DefaultSeed_ShouldHave_DeveloperWithoutCodeExporter_AndDedicatedCodeExporterRole()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ASQA_RBAC_TEST_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var mockEnv = new Mock<IWebHostEnvironment>();
            mockEnv.Setup(e => e.ContentRootPath).Returns(tempDir);

            var provider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(tempDir, "keys")));
            var rbac = new RbacSecurityService(provider, mockEnv.Object);

            var db = rbac.GetDatabase();

            var devRole = db.Roles.FirstOrDefault(r => r.Name == "Developer");
            Assert.NotNull(devRole);
            Assert.DoesNotContain(RbacSecurityService.PermCodeExporter, devRole.Permissions);
            Assert.Contains(RbacSecurityService.PermPrEngine, devRole.Permissions);

            var exporterRole = db.Roles.FirstOrDefault(r => r.Name == "Code Exporter");
            Assert.NotNull(exporterRole);
            Assert.Contains(RbacSecurityService.PermCodeExporter, exporterRole.Permissions);

            var adminRole = db.Roles.FirstOrDefault(r => r.Name == "Admin");
            var qaRole = db.Roles.FirstOrDefault(r => r.Name == "Quality Reviewer");
            Assert.True(adminRole?.IsFixed);
            Assert.True(qaRole?.IsFixed);

            bool created = rbac.CreateRole("Security Auditor", "Inspects security", new List<string> { RbacSecurityService.PermReports });
            Assert.True(created);

            bool deleted = rbac.DeleteRole("Security Auditor");
            Assert.True(deleted);

            bool deletedAdmin = rbac.DeleteRole("Admin");
            Assert.False(deletedAdmin);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void GetDatabase_ShouldCreateBackupFile_WhenEncryptedStorageCannotBeDecrypted()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ASQA_RBAC_BACKUP_TEST_" + Guid.NewGuid().ToString("N"));
        string appData = Path.Combine(tempDir, "App_Data");
        Directory.CreateDirectory(appData);

        try
        {
            File.WriteAllBytes(Path.Combine(appData, "security_db.enc"), new byte[] { 1, 2, 3, 4, 5 });

            var mockEnv = new Mock<IWebHostEnvironment>();
            mockEnv.Setup(e => e.ContentRootPath).Returns(tempDir);

            var provider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(tempDir, "keys")));
            var rbac = new RbacSecurityService(provider, mockEnv.Object);

            var db = rbac.GetDatabase();
            Assert.NotEmpty(db.Roles);

            var backups = Directory.GetFiles(appData, "security_db.enc.bak_*");
            Assert.NotEmpty(backups);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}
