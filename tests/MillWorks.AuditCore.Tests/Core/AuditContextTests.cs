using MillWorks.AuditCore.Abstractions.Services;

namespace MillWorks.AuditCore.Tests.Core;

[TestFixture]
[Category("Unit")]
public class AuditContextTests
{
    private AuditContext _context;

    [SetUp]
    public void Setup()
    {
        _context = new AuditContext();
    }

    [Test]
    public void SetData_StoresValue_RetrievableByKey()
    {
        _context.SetData("key1", "value1");

        var result = _context.GetData<string>("key1");

        Assert.That(result, Is.EqualTo("value1"));
    }

    [Test]
    public void GetData_NonExistentKey_ReturnsDefault()
    {
        var stringResult = _context.GetData<string>("missing");
        var intResult = _context.GetData<int>("missing");

        Assert.That(stringResult, Is.Null);
        Assert.That(intResult, Is.EqualTo(0));
    }

    [Test]
    public void SetData_DuplicateKey_OverwritesValue()
    {
        _context.SetData("key1", "original");
        _context.SetData("key1", "updated");

        var result = _context.GetData<string>("key1");

        Assert.That(result, Is.EqualTo("updated"));
    }

    [Test]
    public void RemoveData_ExistingKey_ReturnsTrueAndRemovesData()
    {
        _context.SetData("key1", "value1");

        var removed = _context.RemoveData("key1");

        Assert.That(removed, Is.True);
        Assert.That(_context.HasData("key1"), Is.False);
    }

    [Test]
    public void RemoveData_NonExistentKey_ReturnsFalse()
    {
        var removed = _context.RemoveData("missing");

        Assert.That(removed, Is.False);
    }

    [Test]
    public void HasData_ExistingKey_ReturnsTrue()
    {
        _context.SetData("key1", "value1");

        Assert.That(_context.HasData("key1"), Is.True);
    }

    [Test]
    public void HasData_NonExistentKey_ReturnsFalse()
    {
        Assert.That(_context.HasData("missing"), Is.False);
    }

    [Test]
    public void Clear_RemovesAllDataAndResetsProperties()
    {
        _context.UserId = Guid.NewGuid();
        _context.AspNetUserId = "asp-123";
        _context.UserEmail = "test@example.com";
        _context.UserFullName = "Test User";
        _context.TenantId = Guid.NewGuid();
        _context.CorrelationId = "corr-123";
        _context.IpAddress = "127.0.0.1";
        _context.UserAgent = "TestAgent";
        _context.RequestPath = "/api/test";
        _context.RequestMethod = "GET";
        _context.OperationId = Guid.NewGuid();
        _context.SetData("key1", "value1");

        _context.Clear();

        Assert.That(_context.UserId, Is.Null);
        Assert.That(_context.AspNetUserId, Is.Null);
        Assert.That(_context.UserEmail, Is.Null);
        Assert.That(_context.UserFullName, Is.Null);
        Assert.That(_context.TenantId, Is.Null);
        Assert.That(_context.CorrelationId, Is.Null);
        Assert.That(_context.IpAddress, Is.Null);
        Assert.That(_context.UserAgent, Is.Null);
        Assert.That(_context.RequestPath, Is.Null);
        Assert.That(_context.RequestMethod, Is.Null);
        Assert.That(_context.OperationId, Is.Null);
        Assert.That(_context.HasData("key1"), Is.False);
    }

    [Test]
    public void AllProperties_SetAndGet_RoundTrip()
    {
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var operationId = Guid.NewGuid();

        _context.UserId = userId;
        _context.AspNetUserId = "asp-456";
        _context.UserEmail = "user@example.com";
        _context.UserFullName = "John Doe";
        _context.TenantId = tenantId;
        _context.CorrelationId = "corr-456";
        _context.IpAddress = "192.168.1.1";
        _context.UserAgent = "Mozilla/5.0";
        _context.RequestPath = "/api/users";
        _context.RequestMethod = "POST";
        _context.OperationId = operationId;

        Assert.That(_context.UserId, Is.EqualTo(userId));
        Assert.That(_context.AspNetUserId, Is.EqualTo("asp-456"));
        Assert.That(_context.UserEmail, Is.EqualTo("user@example.com"));
        Assert.That(_context.UserFullName, Is.EqualTo("John Doe"));
        Assert.That(_context.TenantId, Is.EqualTo(tenantId));
        Assert.That(_context.CorrelationId, Is.EqualTo("corr-456"));
        Assert.That(_context.IpAddress, Is.EqualTo("192.168.1.1"));
        Assert.That(_context.UserAgent, Is.EqualTo("Mozilla/5.0"));
        Assert.That(_context.RequestPath, Is.EqualTo("/api/users"));
        Assert.That(_context.RequestMethod, Is.EqualTo("POST"));
        Assert.That(_context.OperationId, Is.EqualTo(operationId));
    }

    [Test]
    public void GetAllData_ReturnsAllCustomData()
    {
        _context.SetData("key1", "value1");
        _context.SetData("key2", 42);

        var allData = _context.GetAllData();

        Assert.That(allData, Has.Count.EqualTo(2));
        Assert.That(allData["key1"], Is.EqualTo("value1"));
        Assert.That(allData["key2"], Is.EqualTo(42));
    }

    [Test]
    public void GetAllData_ReturnsCopy_ModificationsDoNotAffectContext()
    {
        _context.SetData("key1", "value1");

        var allData = _context.GetAllData();
        allData["key2"] = "injected";

        Assert.That(_context.HasData("key2"), Is.False);
    }

    [Test]
    public void GetData_WrongType_ReturnsDefault()
    {
        _context.SetData("key1", "string_value");

        var result = _context.GetData<int>("key1");

        Assert.That(result, Is.EqualTo(0));
    }

    [Test]
    public void SetData_ComplexObject_StoredAndRetrieved()
    {
        var data = new { Name = "Test", Value = 123 };

        _context.SetData("complex", data);

        var result = _context.GetData<object>("complex");

        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.EqualTo(data));
    }
}
