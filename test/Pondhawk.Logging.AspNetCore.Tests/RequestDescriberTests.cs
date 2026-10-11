// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Shouldly;
using Xunit;

namespace Pondhawk.Logging.AspNetCore.Tests;

/// <summary>Tests the pieces of the request dump on their own: masking, and which bodies count as text.</summary>
public class RequestDescriberTests
{
    private static HashSet<string> Names(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData("a=1&b=2", "a=1&b=***(1)")]
    [InlineData("B=22&a=1", "B=***(2)&a=1")]
    [InlineData("b=&a=1", "b=***(0)&a=1")]
    [InlineData("b&a=1", "b&a=1")]
    [InlineData("b=1&b=two", "b=***(1)&b=***(3)")]
    [InlineData("b=x=y", "b=***(3)")]
    [InlineData("%62=1", "%62=***(1)")]
    [InlineData("a=1&&b=2", "a=1&&b=***(1)")]
    [InlineData("", "")]
    public void MaskPairs_masks_only_the_named_values(string pairs, string expected) =>
        RequestDescriber.MaskPairs(pairs, Names("b")).ShouldBe(expected);

    [Fact]
    public void MaskPairs_matches_a_name_sent_with_a_plus_for_a_space() =>
        RequestDescriber.MaskPairs("pass+word=x", Names("pass word")).ShouldBe("pass+word=***(1)");

    [Fact]
    public void MaskPairs_with_nothing_named_returns_the_text_as_sent() =>
        RequestDescriber.MaskPairs("a=1&b=2", Names()).ShouldBe("a=1&b=2");

    [Theory]
    [InlineData("Authorization", "Bearer abc", "Bearer ***(3)")]
    [InlineData("authorization", "abc", "***(3)")]
    [InlineData("Proxy-Authorization", "Basic dXNlcg==", "Basic ***(8)")]
    [InlineData("Cookie", "a=1; b=2", "***(8)")]
    [InlineData("X-Normal", "visible", "visible")]
    public void Header_masks_a_named_header_and_keeps_an_authorization_scheme(string name, string value, string expected) =>
        RequestDescriber.Header(name, value, Names("Authorization", "Proxy-Authorization", "Cookie")).ShouldBe(expected);

    [Theory]
    [InlineData("application/json", true)]
    [InlineData("application/json; charset=utf-8", true)]
    [InlineData("application/problem+json", true)]
    [InlineData("application/xml", true)]
    [InlineData("application/soap+xml", true)]
    [InlineData("text/plain", true)]
    [InlineData("TEXT/HTML", true)]
    [InlineData("application/x-www-form-urlencoded", true)]
    [InlineData("multipart/form-data; boundary=x", false)]
    [InlineData("application/octet-stream", false)]
    [InlineData("image/png", false)]
    [InlineData("not a content type", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsText_is_true_for_json_xml_text_and_form_bodies_only(string contentType, bool expected) =>
        RequestDescriber.IsText(contentType).ShouldBe(expected);

    [Theory]
    [InlineData("01JABCDEF0123456789", true)]
    [InlineData("a-b_c.d", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("a b", false)]
    [InlineData("a\nb", false)]
    [InlineData("a/b", false)]
    public void IsWellFormed_accepts_only_letters_digits_dash_underscore_and_dot(string id, bool expected) =>
        RequestCorrelation.IsWellFormed(id).ShouldBe(expected);

    [Fact]
    public void IsWellFormed_accepts_64_characters_and_refuses_65()
    {
        RequestCorrelation.IsWellFormed(new string('a', 64)).ShouldBeTrue();
        RequestCorrelation.IsWellFormed(new string('a', 65)).ShouldBeFalse();
    }
}
