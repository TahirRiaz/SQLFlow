using SqlFlow.Core;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The file-origin decomposition: every file endpoint any flow type can name resolves to its canonical parent
/// (storage account, SFTP server, UNC share, or the local filesystem) with a clean container/folder/leaf
/// split. Covers the identity shapes the lineage layer actually stores plus the malformed and adversarial
/// inputs the parser must survive without throwing.
/// </summary>
public sealed class FileOriginTests
{
    [Fact]
    public void Azure_CanonicalIdentity_SplitsAccountContainerFolderLeaf()
    {
        var origin = FileOrigin.Parse("az://dwdatalakeprod/datalakev2/raw/boatbooking/history/detail");

        Assert.Equal(FileOriginKind.AzureStorage, origin.Kind);
        Assert.Equal("dwdatalakeprod", origin.Origin);
        Assert.Equal("datalakev2", origin.Container);
        Assert.Equal("raw/boatbooking/history", origin.Path);
        Assert.Equal("detail", origin.Name);
    }

    [Fact]
    public void Azure_LeafDirectlyUnderContainer_HasNoPath()
    {
        var origin = FileOrigin.Parse("az://dwstorageboatbookingprod/boatbooking/DETAIL");

        Assert.Equal("dwstorageboatbookingprod", origin.Origin);
        Assert.Equal("boatbooking", origin.Container);
        Assert.Null(origin.Path);
        Assert.Equal("DETAIL", origin.Name);
    }

    [Fact]
    public void Azure_ContainerRoot_TreatsContainerAsLeaf()
    {
        var origin = FileOrigin.Parse("az://acct/landing");

        Assert.Equal("acct", origin.Origin);
        Assert.Null(origin.Container);
        Assert.Null(origin.Path);
        Assert.Equal("landing", origin.Name);
    }

    [Theory]
    // Raw ADLS/Blob spellings all fold to the same account/container identity, so a copy target written in
    // abfss and read back over https group as one origin.
    [InlineData("abfss://datalakev2@dwdatalakeprod.dfs.core.windows.net/raw/x")]
    [InlineData("https://dwdatalakeprod.blob.core.windows.net/datalakev2/raw/x")]
    [InlineData("wasbs://datalakev2@dwdatalakeprod.blob.core.windows.net/raw/x")]
    public void Azure_RawSpellings_FoldToTheCanonicalAccountAndContainer(string location)
    {
        var origin = FileOrigin.Parse(location);

        Assert.Equal(FileOriginKind.AzureStorage, origin.Kind);
        Assert.Equal("dwdatalakeprod", origin.Origin);
        Assert.Equal("datalakev2", origin.Container);
        Assert.Equal("x", origin.Name);
    }

    [Fact]
    public void Azure_TrailingSlashAndDoubleSlash_AreIgnored()
    {
        var origin = FileOrigin.Parse("az://acct/cont/raw//history/");

        Assert.Equal("acct", origin.Origin);
        Assert.Equal("cont", origin.Container);
        Assert.Equal("raw", origin.Path);
        Assert.Equal("history", origin.Name);
    }

    [Fact]
    public void Azure_SasTokenQuery_IsStrippedFromTheLeaf()
    {
        var origin = FileOrigin.Parse("az://acct/cont/raw/orders.parquet?sv=2021&sig=abc");

        Assert.Equal("raw", origin.Path);
        Assert.Equal("orders.parquet", origin.Name);
    }

    [Fact]
    public void Sftp_HostPortIsTheOrigin_PathIsFolderAndLeaf()
    {
        var origin = FileOrigin.Parse("sftp://ftp.acme.com:22/exports/daily/orders.csv");

        Assert.Equal(FileOriginKind.Sftp, origin.Kind);
        Assert.Equal("ftp.acme.com:22", origin.Origin);
        Assert.Null(origin.Container);
        Assert.Equal("exports/daily", origin.Path);
        Assert.Equal("orders.csv", origin.Name);
    }

    [Fact]
    public void Sftp_BareRemotePathDot_LeavesACleanServerNode()
    {
        // The SFTP collector emits 'sftp://host:port' + RemotePath; a RemotePath of '.' leaves a trailing dot.
        var origin = FileOrigin.Parse("sftp://ftp.acme.com:22.");

        Assert.Equal(FileOriginKind.Sftp, origin.Kind);
        Assert.Equal("ftp.acme.com:22", origin.Origin);
        Assert.Equal("ftp.acme.com:22", origin.Name);
    }

    [Fact]
    public void Sftp_UserInfoAndCase_AreNormalizedAway()
    {
        var origin = FileOrigin.Parse("sftp://svc-user@FTP.Acme.Com:2222/In/File.CSV");

        Assert.Equal("ftp.acme.com:2222", origin.Origin);
        Assert.Equal("In", origin.Path);
        Assert.Equal("File.CSV", origin.Name);
    }

    [Fact]
    public void Local_RelativePath_GroupsUnderTheFilesystem()
    {
        var origin = FileOrigin.Parse("data/incoming/orders.csv");

        Assert.Equal(FileOriginKind.Local, origin.Kind);
        Assert.Equal(FileOrigin.LocalOrigin, origin.Origin);
        Assert.Null(origin.Container);
        Assert.Equal("data/incoming", origin.Path);
        Assert.Equal("orders.csv", origin.Name);
    }

    [Fact]
    public void Local_SingleSegment_IsALeafWithNoPath()
    {
        var origin = FileOrigin.Parse("data/");

        Assert.Equal(FileOrigin.LocalOrigin, origin.Origin);
        Assert.Null(origin.Path);
        Assert.Equal("data", origin.Name);
    }

    [Fact]
    public void Local_BackslashesAndDotSegments_AreNormalized()
    {
        var origin = FileOrigin.Parse(@"data\.\sub\file.csv");

        Assert.Equal(FileOrigin.LocalOrigin, origin.Origin);
        Assert.Equal("data/sub", origin.Path);
        Assert.Equal("file.csv", origin.Name);
    }

    [Fact]
    public void Local_WindowsDriveLetter_IsNotMistakenForAScheme()
    {
        var origin = FileOrigin.Parse("C:/data/x.csv");

        Assert.Equal(FileOriginKind.Local, origin.Kind);
        Assert.Equal(FileOrigin.LocalOrigin, origin.Origin);
        Assert.Equal("C:/data", origin.Path);
        Assert.Equal("x.csv", origin.Name);
    }

    [Fact]
    public void Unc_ServerIsTheOrigin_ShareIsTheContainer()
    {
        var origin = FileOrigin.Parse(@"\\fileserver01\dropzone\boatbooking\orders.csv");

        Assert.Equal(FileOriginKind.NetworkShare, origin.Kind);
        Assert.Equal("fileserver01", origin.Origin);
        Assert.Equal("dropzone", origin.Container);
        Assert.Equal("boatbooking", origin.Path);
        Assert.Equal("orders.csv", origin.Name);
    }

    [Fact]
    public void S3_IsClassifiedAsAmazon_BucketIsTheOrigin()
    {
        var origin = FileOrigin.Parse("s3://my-bucket/prefix/file.parquet");

        Assert.Equal(FileOriginKind.AmazonS3, origin.Kind);
        Assert.Equal("my-bucket", origin.Origin);
        Assert.Equal("prefix", origin.Path);
        Assert.Equal("file.parquet", origin.Name);
    }

    [Theory]
    [InlineData("gs://data-lake/raw/x", "data-lake")]
    [InlineData("gcs://data-lake/raw/x", "data-lake")]
    public void GoogleCloud_IsClassified_BucketIsTheOrigin(string location, string bucket)
    {
        var origin = FileOrigin.Parse(location);

        Assert.Equal(FileOriginKind.GoogleCloud, origin.Kind);
        Assert.Equal(bucket, origin.Origin);
        Assert.Equal("x", origin.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_YieldsAnUnknownLeaf_WithoutThrowing(string identity)
    {
        var origin = FileOrigin.Parse(identity);

        Assert.Equal(FileOrigin.UnknownOrigin, origin.Origin);
        Assert.Equal(FileOrigin.UnknownOrigin, origin.Name);
    }

    [Theory]
    // Adversarial and degenerate inputs: the contract is "never throw, never an empty Origin/Name".
    [InlineData("az://")]
    [InlineData("sftp://")]
    [InlineData("://noscheme/path")]
    [InlineData("http://")]
    [InlineData("weird:notaslash")]
    [InlineData("/")]
    [InlineData("///")]
    public void Malformed_NeverThrows_AndAlwaysNamesAnOriginAndLeaf(string identity)
    {
        var origin = FileOrigin.Parse(identity);

        Assert.False(string.IsNullOrEmpty(origin.Origin));
        Assert.False(string.IsNullOrEmpty(origin.Name));
    }
}
