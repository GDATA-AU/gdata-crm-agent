using System.Security.Cryptography;
using System.Text;
using CrmAgent.Services;

namespace CrmAgent.Tests;

public class HashServiceTests
{
    [Fact]
    public void Produces64CharHexSha256Digest()
    {
        var row = new Dictionary<string, object?>
        {
            ["Name"] = "Smith",
            ["DOB"] = "1985-01-01",
            ["Email"] = "john@example.com",
        };
        var hash = HashService.ComputeRowHash(row, ["Name", "DOB", "Email"]);
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Fact]
    public void IsDeterministicForSameInput()
    {
        var row = new Dictionary<string, object?> { ["A"] = "foo", ["B"] = "bar", ["C"] = "baz" };
        string[] fields = ["A", "B", "C"];
        Assert.Equal(
            HashService.ComputeRowHash(row, fields),
            HashService.ComputeRowHash(row, fields));
    }

    [Fact]
    public void ChangesWhenFieldValueChanges()
    {
        var row1 = new Dictionary<string, object?> { ["Name"] = "Smith", ["DOB"] = "1985-01-01" };
        var row2 = new Dictionary<string, object?> { ["Name"] = "Jones", ["DOB"] = "1985-01-01" };
        string[] fields = ["Name", "DOB"];
        Assert.NotEqual(
            HashService.ComputeRowHash(row1, fields),
            HashService.ComputeRowHash(row2, fields));
    }

    [Fact]
    public void TrimsStringValuesBeforeHashing()
    {
        var row1 = new Dictionary<string, object?> { ["Name"] = "Smith  ", ["DOB"] = "1985-01-01" };
        var row2 = new Dictionary<string, object?> { ["Name"] = "Smith", ["DOB"] = "1985-01-01" };
        string[] fields = ["Name", "DOB"];
        Assert.Equal(
            HashService.ComputeRowHash(row1, fields),
            HashService.ComputeRowHash(row2, fields));
    }

    [Fact]
    public void TreatsNullAsEmptyString()
    {
        var row1 = new Dictionary<string, object?> { ["Name"] = "Smith", ["DOB"] = null };
        var row2 = new Dictionary<string, object?> { ["Name"] = "Smith", ["DOB"] = "" };
        string[] fields = ["Name", "DOB"];
        Assert.Equal(
            HashService.ComputeRowHash(row1, fields),
            HashService.ComputeRowHash(row2, fields));
    }

    [Fact]
    public void HandlesMissingFieldsGracefully()
    {
        var row = new Dictionary<string, object?> { ["Name"] = "Smith" };
        string[] fields = ["Name", "MissingField"];
        var hash = HashService.ComputeRowHash(row, fields);
        var expected = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes("Smith|")));
        Assert.Equal(expected, hash);
    }

    [Fact]
    public void HandlesNumericValues()
    {
        var row = new Dictionary<string, object?> { ["id"] = 12345, ["score"] = 98.6 };
        string[] fields = ["id", "score"];
        var hash = HashService.ComputeRowHash(row, fields);
        var expected = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes("12345|98.6")));
        Assert.Equal(expected, hash);
    }

    [Fact]
    public void ProducesExpectedHashForPathwayFields()
    {
        var row = new Dictionary<string, object?>
        {
            ["Given_Name"] = "John  ",
            ["Surname"] = "Smith  ",
            ["DOB"] = "1985-03-15",
            ["Email"] = "john@example.com",
            ["Phone_H"] = "0398765432",
            ["Phone_M"] = "0412345678",
            ["Title"] = "Mr",
            ["Gender"] = "M",
            ["Category_Code"] = "IND",
            ["IS_Company"] = "F",
            ["IS_Private"] = "F",
            ["IS_Deceased"] = "F",
        };
        string[] fields =
        [
            "Given_Name", "Surname", "DOB", "Email",
            "Phone_H", "Phone_M", "Title", "Gender",
            "Category_Code", "IS_Company", "IS_Private", "IS_Deceased",
        ];

        var hash = HashService.ComputeRowHash(row, fields);
        var expected = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                "John|Smith|1985-03-15|john@example.com|0398765432|0412345678|Mr|M|IND|F|F|F")));
        Assert.Equal(expected, hash);
    }
}
