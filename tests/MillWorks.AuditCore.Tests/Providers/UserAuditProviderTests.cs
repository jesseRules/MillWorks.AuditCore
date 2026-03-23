using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Providers.Implementations;

namespace MillWorks.AuditCore.Tests.Providers;

[TestFixture]
[Category("Unit")]
public class UserAuditProviderTests
{
    private Mock<IHttpContextAccessor> _mockHttpContextAccessor;
    private Mock<IAuditEventFactory> _mockEventFactory;
    private Mock<ILoggerFactory> _mockLoggerFactory;
    private UserAuditProvider _provider;

    [SetUp]
    public void Setup()
    {
        _mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        _mockEventFactory = new Mock<IAuditEventFactory>();
        _mockLoggerFactory = new Mock<ILoggerFactory>();
        _mockLoggerFactory.Setup(static f => f.CreateLogger(It.IsAny<string>()))
            .Returns(new Mock<ILogger>().Object);

        _provider = new UserAuditProvider(
            _mockHttpContextAccessor.Object,
            _mockEventFactory.Object,
            _mockLoggerFactory.Object);
    }

    [Test]
    public void EntityType_ReturnsUser()
    {
        Assert.That(_provider.EntityType, Is.EqualTo("User"));
    }

    [Test]
    public async Task ShouldAuditAsync_AlwaysReturnsTrue()
    {
        var entity = new { Id = Guid.NewGuid(), Email = "test@example.com" };

        var result = await _provider.ShouldAuditAsync("Create", entity);

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task ShouldAuditAsync_AnyAction_ReturnsTrue()
    {
        var entity = new { Id = Guid.NewGuid() };

        Assert.That(await _provider.ShouldAuditAsync("Create", entity), Is.True);
        Assert.That(await _provider.ShouldAuditAsync("Update", entity), Is.True);
        Assert.That(await _provider.ShouldAuditAsync("Delete", entity), Is.True);
    }

    [Test]
    public async Task EnrichAuditEventAsync_WithDictionary_AddsUserFields()
    {
        var auditEvent = new AuditEvent();
        var entityDict = new Dictionary<string, object>
        {
            { "Id", "aspnet-user-123" },
            { "Email", "user@example.com" },
            { "UserName", "testuser" },
            { "IsActive", true },
            { "TwoFactorEnabled", false }
        };

        await _provider.EnrichAuditEventAsync(auditEvent, entityDict);

        Assert.That(auditEvent.CustomFields["AspNetUserId"], Is.EqualTo("aspnet-user-123"));
        Assert.That(auditEvent.CustomFields["Email"], Is.EqualTo("user@example.com"));
        Assert.That(auditEvent.CustomFields["UserName"], Is.EqualTo("testuser"));
        Assert.That(auditEvent.CustomFields["IsActive"], Is.EqualTo(true));
        Assert.That(auditEvent.CustomFields["TwoFactorEnabled"], Is.EqualTo(false));
    }

    [Test]
    public async Task EnrichAuditEventAsync_WithGuidId_SetsUserId()
    {
        var auditEvent = new AuditEvent();
        var userId = Guid.NewGuid();
        var entityDict = new Dictionary<string, object>
        {
            { "Id", userId }
        };

        await _provider.EnrichAuditEventAsync(auditEvent, entityDict);

        Assert.That(auditEvent.CustomFields["UserId"], Is.EqualTo(userId));
    }

    [Test]
    public async Task EnrichAuditEventAsync_WithNames_CombinesFullName()
    {
        var auditEvent = new AuditEvent();
        var entityDict = new Dictionary<string, object>
        {
            { "Id", Guid.NewGuid() },
            { "FirstName", "John" },
            { "LastName", "Doe" }
        };

        await _provider.EnrichAuditEventAsync(auditEvent, entityDict);

        Assert.That(auditEvent.CustomFields["FullName"], Is.EqualTo("John Doe"));
    }

    [Test]
    public async Task EnrichAuditEventAsync_WithRefreshToken_MasksAsHasRefreshToken()
    {
        var auditEvent = new AuditEvent();
        var entityDict = new Dictionary<string, object>
        {
            { "Id", "aspnet-123" },
            { "RefreshToken", "some-secret-token" }
        };

        await _provider.EnrichAuditEventAsync(auditEvent, entityDict);

        Assert.That(auditEvent.CustomFields["HasRefreshToken"], Is.True);
    }

    [Test]
    public void GetChanges_SensitiveProperties_AreExcluded()
    {
        var oldValues = new Dictionary<string, object>
        {
            { "Email", "old@example.com" },
            { "PasswordHash", "old-hash" },
            { "SecurityStamp", "old-stamp" }
        };

        var newEntity = new TestUserEntity
        {
            Email = "new@example.com",
            PasswordHash = "new-hash",
            SecurityStamp = "new-stamp"
        };

        var changes = _provider.GetChanges(oldValues, newEntity);

        Assert.That(changes.ContainsKey("Email"), Is.True);
        Assert.That(changes.ContainsKey("PasswordHash"), Is.False);
        Assert.That(changes.ContainsKey("SecurityStamp"), Is.False);
    }

    [Test]
    public void GetChanges_NullOldValues_ReturnsEmpty()
    {
        var changes = _provider.GetChanges(null, new TestUserEntity());

        Assert.That(changes, Is.Empty);
    }

    [Test]
    public void GetChanges_NullNewValues_ReturnsEmpty()
    {
        var changes = _provider.GetChanges(new Dictionary<string, object>(), null);

        Assert.That(changes, Is.Empty);
    }

    [Test]
    public async Task CreateAuditEventAsync_NullEntity_ThrowsArgumentNullException()
    {
        Assert.ThrowsAsync<ArgumentNullException>(
            async () => await _provider.CreateAuditEventAsync("Create", null!));
    }

    [Test]
    public async Task CreateAuditEventAsync_ValidEntity_CreatesEvent()
    {
        var entity = new TestUserEntity { Email = "user@example.com" };
        var expectedEvent = new AuditEvent { EventType = "User.Create" };

        _mockEventFactory.Setup(f => f.CreateEntityEvent("User", "Create", entity, null))
            .Returns(expectedEvent);
        _mockHttpContextAccessor.Setup(static h => h.HttpContext).Returns(static () => null!);

        var result = await _provider.CreateAuditEventAsync("Create", entity);

        Assert.That(result, Is.Not.Null);
    }

    /// <summary>
    /// Test entity to simulate user properties
    /// </summary>
    private class TestUserEntity
    {
        public string Email { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public string SecurityStamp { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
    }
}
