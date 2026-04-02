using MillWorks.AuditCore.Services.Encryption;

namespace MillWorks.AuditCore.Tests.Services.Encryption;

/// <summary>
/// Tests for FieldKeyDerivation verifying HKDF-SHA256 key derivation correctness:
/// determinism, uniqueness per field/version, and output size.
/// </summary>
[TestFixture]
[Category("Unit")]
public class FieldKeyDerivationTests
{
    private static readonly byte[] MasterKey = new byte[32]
    {
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
        0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10,
        0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18,
        0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20
    };

    #region DeriveFieldKey(masterKey, fieldName) — Two-Arg Overload

    [Test]
    public void DeriveFieldKey_ReturnsThirtyTwoBytes()
    {
        var key = FieldKeyDerivation.DeriveFieldKey(MasterKey, "SSN");
        Assert.That(key, Has.Length.EqualTo(32));
    }

    [Test]
    public void DeriveFieldKey_IsDeterministic()
    {
        var key1 = FieldKeyDerivation.DeriveFieldKey(MasterKey, "SSN");
        var key2 = FieldKeyDerivation.DeriveFieldKey(MasterKey, "SSN");

        Assert.That(key1, Is.EqualTo(key2));
    }

    [Test]
    public void DeriveFieldKey_DifferentFieldNames_ProduceDifferentKeys()
    {
        var ssnKey = FieldKeyDerivation.DeriveFieldKey(MasterKey, "SSN");
        var emailKey = FieldKeyDerivation.DeriveFieldKey(MasterKey, "Email");

        Assert.That(ssnKey, Is.Not.EqualTo(emailKey));
    }

    [Test]
    public void DeriveFieldKey_DifferentMasterKeys_ProduceDifferentKeys()
    {
        var altMaster = new byte[32];
        Array.Fill(altMaster, (byte)0xFF);

        var key1 = FieldKeyDerivation.DeriveFieldKey(MasterKey, "SSN");
        var key2 = FieldKeyDerivation.DeriveFieldKey(altMaster, "SSN");

        Assert.That(key1, Is.Not.EqualTo(key2));
    }

    [Test]
    public void DeriveFieldKey_CaseSensitive()
    {
        var lower = FieldKeyDerivation.DeriveFieldKey(MasterKey, "ssn");
        var upper = FieldKeyDerivation.DeriveFieldKey(MasterKey, "SSN");

        Assert.That(lower, Is.Not.EqualTo(upper));
    }

    #endregion

    #region DeriveFieldKey(masterKey, fieldName, keyVersion) — Three-Arg Overload

    [Test]
    public void DeriveFieldKeyWithVersion_ReturnsThirtyTwoBytes()
    {
        var key = FieldKeyDerivation.DeriveFieldKey(MasterKey, "SSN", "v1");
        Assert.That(key, Has.Length.EqualTo(32));
    }

    [Test]
    public void DeriveFieldKeyWithVersion_IsDeterministic()
    {
        var key1 = FieldKeyDerivation.DeriveFieldKey(MasterKey, "SSN", "v1");
        var key2 = FieldKeyDerivation.DeriveFieldKey(MasterKey, "SSN", "v1");

        Assert.That(key1, Is.EqualTo(key2));
    }

    [Test]
    public void DeriveFieldKeyWithVersion_DifferentVersions_ProduceDifferentKeys()
    {
        var v1 = FieldKeyDerivation.DeriveFieldKey(MasterKey, "SSN", "v1");
        var v2 = FieldKeyDerivation.DeriveFieldKey(MasterKey, "SSN", "v2");

        Assert.That(v1, Is.Not.EqualTo(v2));
    }

    [Test]
    public void DeriveFieldKeyWithVersion_DifferentFromUnversionedOverload()
    {
        var unversioned = FieldKeyDerivation.DeriveFieldKey(MasterKey, "SSN");
        var versioned = FieldKeyDerivation.DeriveFieldKey(MasterKey, "SSN", "v1");

        Assert.That(unversioned, Is.Not.EqualTo(versioned));
    }

    [Test]
    public void DeriveFieldKeyWithVersion_EmptyVersion_DiffersFromNonEmpty()
    {
        var empty = FieldKeyDerivation.DeriveFieldKey(MasterKey, "SSN", "");
        var v1 = FieldKeyDerivation.DeriveFieldKey(MasterKey, "SSN", "v1");

        Assert.That(empty, Is.Not.EqualTo(v1));
    }

    #endregion
}
