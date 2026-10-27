using System.Security.Cryptography;
using System.Text;
using Cuebot.Obs.Protocol;

namespace Cuebot.Obs.Tests;

public class ObsAuthTests
{
    [Fact]
    public void Matches_the_example_from_the_protocol_documentation()
    {
        var auth = ObsAuth.Compute("supersecretpassword",
            "lM1GncleQOaCu9lT1yeUZhFYnqhsLLP1G5lAGo3ixaI=", "+IxH4CnCiqpX1rM9scsNynZzbOe4KhDeYcTNS3PDaeY=");

        Assert.Equal("1Ct943GAT+6YQUUX47Ia/ncufilbe6+oD6lY+5kaCu4=", auth);
    }

    [Theory]
    [InlineData("hunter2", "c2FsdA==", "Y2hhbGxlbmdl")]
    [InlineData("pässwörd with spaces", "", "x")]
    public void Follows_the_spec_formula(string password, string salt, string challenge)
    {
        static string Base64Sha256(string s) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
        var expected = Base64Sha256(Base64Sha256(password + salt) + challenge);

        Assert.Equal(expected, ObsAuth.Compute(password, salt, challenge));
    }
}
