using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Amberline.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace Amberline.Tests.Air;

public static class AirRegistrationPageChecks
{



    private static (string Field, string Value) Token(string html)
    {
        var match = Regex.Match(html, "<input type=hidden name=\"([^\"]+)\" value=\"([^\"]+)\">");
        TestAssert.True(match.Success, "registration must provide an antiforgery field");
        return (WebUtility.HtmlDecode(match.Groups[1].Value), WebUtility.HtmlDecode(match.Groups[2].Value));
    }
}
