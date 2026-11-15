using System.Linq;
using System.Text.RegularExpressions;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>U250: generated passwords are strong, readable and typed back exactly.</summary>
public sealed class PasswordGeneratorTests
{
    [Fact]
    public void A_generated_password_is_five_groups_of_five_without_look_alikes()
    {
        for (var i = 0; i < 200; i++)
        {
            var password = PasswordGenerator.Create();
            Assert.Matches(new Regex("^[a-z2-9]{5}(-[a-z2-9]{5}){4}$"), password);
            Assert.DoesNotContain(password, c => "01ilo".Contains(c));
        }
    }

    [Fact]
    public void Generated_passwords_differ_and_use_the_whole_alphabet()
    {
        var many = Enumerable.Range(0, 500).Select(_ => PasswordGenerator.Create()).ToList();
        Assert.Equal(many.Count, many.Distinct().Count());
        var used = many.SelectMany(p => p.Replace("-", "")).Distinct().Count();
        Assert.Equal(PasswordGenerator.Alphabet.Length, used);
        // About 124 bits: 25 characters from 31.
        Assert.True(25 * System.Math.Log2(PasswordGenerator.Alphabet.Length) > 120);
    }

    [Theory]
    [InlineData("k7mqd-x3vth-abcde-fghjk-mnpqr", true)]
    [InlineData("  k7mqd-x3vth-abcde-fghjk-mnpqr ", true)]
    [InlineData("K7MQD-X3VTH-ABCDE-FGHJK-MNPQR", true)]
    [InlineData("k7mqdx3vthabcdefghjkmnpqr", false)]
    [InlineData("k7mqd-x3vth-abcde-fghjk-mnpq", false)]
    [InlineData("k7mqd-x3vth-abcde-fghjk-mnpqs", false)]
    [InlineData("", false)]
    public void Typing_it_back_must_match_except_spaces_and_case(string typed, bool matches)
        => Assert.Equal(matches, PasswordGenerator.Matches("k7mqd-x3vth-abcde-fghjk-mnpqr", typed));
}
