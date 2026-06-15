using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Providers.Base;

namespace MillWorks.AuditCore.Tests.Providers;

/// <summary>
/// Unit tests for BaseAuditProvider (section 2.16a of the test plan).
/// Uses a concrete TestableAuditProvider to exercise abstract base behavior.
/// </summary>
[TestFixture]
[Category("Unit")]
public class BaseAuditProviderTests
{
    private Mock<IHttpContextAccessor> _mockHttpContextAccessor;
    private Mock<IAuditEventFactory> _mockEventFactory;
    private Mock<ILoggerFactory> _mockLoggerFactory;
    private TestableAuditProvider _provider;

    [SetUp]
    public void Setup()
    {
        _mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        _mockEventFactory = new Mock<IAuditEventFactory>();
        _mockLoggerFactory = new Mock<ILoggerFactory>();
        _mockLoggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>()))
            .Returns(new Mock<ILogger>().Object);

        _provider = new TestableAuditProvider(
            _mockHttpContextAccessor.Object,
            _mockEventFactory.Object,
            _mockLoggerFactory.Object);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 1. EntityType — returns the value from the concrete provider
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public void EntityType_ReturnsConfiguredType()
    {
        Assert.That(_provider.EntityType, Is.EqualTo("TestEntity"));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 2. CreateAuditEventAsync — returns a populated event with HTTP context
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task CreateAuditEventAsync_ReturnsPopulatedEvent()
    {
        // Arrange
        var entity = new SampleEntity { Id = 1, Name = "Widget" };
        var expectedEvent = new AuditEvent { EventType = "TestEntity.Create" };

        // Entity is sanitized to a Dictionary before passing to CreateEntityEvent
        _mockEventFactory
            .Setup(f => f.CreateEntityEvent("TestEntity", "Create", It.IsAny<Dictionary<string, object?>>(), null))
            .Returns(expectedEvent);

        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.168.1.42");
        httpContext.Request.Headers["User-Agent"] = "TestAgent/1.0";
        httpContext.TraceIdentifier = "trace-abc-123";
        _mockHttpContextAccessor.Setup(static h => h.HttpContext).Returns(httpContext);

        // Act
        var result = await _provider.CreateAuditEventAsync("Create", entity);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.EventType, Is.EqualTo("TestEntity.Create"));
        Assert.That(result.IpAddress, Is.EqualTo("192.168.1.42"));
        Assert.That(result.UserAgent, Is.EqualTo("TestAgent/1.0"));
        Assert.That(result.RequestId, Is.EqualTo("trace-abc-123"));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 3. ShouldAuditAsync — delegates to concrete provider
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ShouldAuditAsync_DelegatesToConcreteProvider()
    {
        var entity = new SampleEntity { Id = 1, Name = "Widget" };

        // TestableAuditProvider always returns true
        var result = await _provider.ShouldAuditAsync("Create", entity);

        Assert.That(result, Is.True);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 4. EnrichAuditEventAsync — base implementation is a no-op
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task EnrichAuditEventAsync_BaseImplementation_DoesNotModifyEvent()
    {
        // Use a provider that does NOT override EnrichAuditEventAsync
        var noEnrichProvider = new NoEnrichAuditProvider(
            _mockHttpContextAccessor.Object,
            _mockEventFactory.Object,
            _mockLoggerFactory.Object);

        var auditEvent = new AuditEvent { EventType = "Test" };
        var originalFieldCount = auditEvent.CustomFields.Count;

        await noEnrichProvider.EnrichAuditEventAsync(auditEvent, new SampleEntity());

        Assert.That(auditEvent.CustomFields, Has.Count.EqualTo(originalFieldCount));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 5. GetChanges — detects property differences between old and new values
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public void GetChanges_DetectsPropertyDifferences()
    {
        var oldValues = new Dictionary<string, object>
        {
            { "Id", 1 },
            { "Name", "OldName" },
            { "Price", 9.99m }
        };

        var newEntity = new SampleEntity { Id = 1, Name = "NewName", Price = 19.99m };

        var changes = _provider.GetChanges(oldValues, newEntity);

        // Id unchanged, Name and Price changed
        Assert.That(changes, Has.Count.EqualTo(2));
        Assert.That(changes.ContainsKey("Name"), Is.True);
        Assert.That(changes.ContainsKey("Price"), Is.True);
        Assert.That(changes.ContainsKey("Id"), Is.False);
    }

    [Test]
    public void GetChanges_NullOldValues_ReturnsEmpty()
    {
        var changes = _provider.GetChanges(null, new SampleEntity());

        Assert.That(changes, Is.Empty);
    }

    [Test]
    public void GetChanges_NullNewValues_ReturnsEmpty()
    {
        var changes = _provider.GetChanges(new Dictionary<string, object>(), null);

        Assert.That(changes, Is.Empty);
    }

    [Test]
    public void GetChanges_BothEntities_ComparesViaReflection()
    {
        var oldEntity = new SampleEntity { Id = 1, Name = "Old", Price = 5.00m };
        var newEntity = new SampleEntity { Id = 1, Name = "New", Price = 5.00m };

        var changes = _provider.GetChanges(oldEntity, newEntity);

        // Only Name changed; Id and Price are the same
        Assert.That(changes, Has.Count.EqualTo(1));
        Assert.That(changes.ContainsKey("Name"), Is.True);
    }

    [Test]
    public async Task CreateAuditEventAsync_WithOldValues_AddsChangesToCustomFields()
    {
        // Arrange
        var oldValues = new Dictionary<string, object>
        {
            { "Id", 1 },
            { "Name", "OldName" },
            { "Price", 9.99m }
        };
        var newEntity = new SampleEntity { Id = 1, Name = "NewName", Price = 19.99m };
        var expectedEvent = new AuditEvent { EventType = "TestEntity.Update" };

        // Entity is sanitized to a Dictionary before passing to CreateEntityEvent
        _mockEventFactory
            .Setup(f => f.CreateEntityEvent("TestEntity", "Update", It.IsAny<Dictionary<string, object?>>(), It.IsAny<Dictionary<string, object?>>()))
            .Returns(expectedEvent);
        _mockHttpContextAccessor.Setup(static h => h.HttpContext).Returns(static () => null!);

        // Act
        var result = await _provider.CreateAuditEventAsync("Update", newEntity, oldValues);

        // Assert
        Assert.That(result.CustomFields.ContainsKey("Changes"), Is.True);
    }

    [Test]
    public async Task CreateAuditEventAsync_NullEntity_ThrowsArgumentNullException()
    {
        Assert.ThrowsAsync<ArgumentNullException>(
            async () => await _provider.CreateAuditEventAsync("Create", null!));
    }

    [Test]
    public async Task CreateAuditEventAsync_NoHttpContext_SkipsRequestFields()
    {
        // Arrange
        var entity = new SampleEntity { Id = 1 };
        var expectedEvent = new AuditEvent { EventType = "TestEntity.Create" };

        // Entity is sanitized to a Dictionary before passing to CreateEntityEvent
        _mockEventFactory
            .Setup(f => f.CreateEntityEvent("TestEntity", "Create", It.IsAny<Dictionary<string, object?>>(), null))
            .Returns(expectedEvent);
        _mockHttpContextAccessor.Setup(static h => h.HttpContext).Returns(static () => null!);

        // Act
        var result = await _provider.CreateAuditEventAsync("Create", entity);

        // Assert — HTTP fields remain null
        Assert.That(result.IpAddress, Is.Null);
        Assert.That(result.UserAgent, Is.Null);
        Assert.That(result.RequestId, Is.Null);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Test helpers
    // ──────────────────────────────────────────────────────────────────────────

    private class SampleEntity
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public decimal Price { get; set; }
    }

    /// <summary>
    /// Concrete provider for testing base class behavior.
    /// </summary>
    private class TestableAuditProvider : BaseAuditProvider
    {
        public TestableAuditProvider(
            IHttpContextAccessor httpContextAccessor,
            IAuditEventFactory eventFactory,
            ILoggerFactory loggerFactory)
            : base(httpContextAccessor, eventFactory, loggerFactory) { }

        public override string EntityType => "TestEntity";

        public override Task<bool> ShouldAuditAsync(string action, object entity) =>
            Task.FromResult(true);
    }

    /// <summary>
    /// Provider that does NOT override EnrichAuditEventAsync, so we can test the base no-op.
    /// </summary>
    private class NoEnrichAuditProvider : BaseAuditProvider
    {
        public NoEnrichAuditProvider(
            IHttpContextAccessor httpContextAccessor,
            IAuditEventFactory eventFactory,
            ILoggerFactory loggerFactory)
            : base(httpContextAccessor, eventFactory, loggerFactory) { }

        public override string EntityType => "NoEnrich";

        public override Task<bool> ShouldAuditAsync(string action, object entity) =>
            Task.FromResult(true);
    }
}
