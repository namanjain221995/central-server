using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Domain.Chrome;

namespace EndpointPlatform.Domain.Tests.Chrome;

/// <summary>
/// The domain's Chrome enums and the wire contract's string sets must be the same
/// sets.
/// </summary>
/// <remarks>
/// <para>
/// The Agent API maps the contract's strings onto these enums by name with a
/// case-sensitive parse, and the domain cannot reference the contract to share the
/// values. If the two drift, a status or install type the agent reports lands on
/// the fallback member (Error, Unknown) and nothing else fails, so the drift would
/// be invisible until someone wondered why every extension on the estate was of
/// unknown origin. This test is the only thing that makes it visible.
/// </para>
/// <para>
/// The extension-id rule is stated in both places for the same reason, and the
/// last test drives both over one corpus.
/// </para>
/// </remarks>
public sealed class ChromeContractAlignmentTests
{
    [Fact]
    public void Every_install_type_the_contract_allows_has_a_domain_member_of_the_same_name()
    {
        var domain = Enum.GetNames<ChromeExtensionInstallType>();

        foreach (var contractValue in InventoryChromeExtension.InstallTypes)
        {
            domain.ShouldContain(
                contractValue,
                $"the contract allows InstallType '{contractValue}' but ChromeExtensionInstallType has no such member, " +
                "so ingestion would store it as Unknown");
        }
    }

    [Fact]
    public void Every_domain_install_type_is_a_value_the_contract_allows()
    {
        foreach (var name in Enum.GetNames<ChromeExtensionInstallType>())
        {
            InventoryChromeExtension.InstallTypes.ShouldContain(
                name,
                $"ChromeExtensionInstallType.{name} can never be reported, because the contract does not allow it");
        }
    }

    [Fact]
    public void Every_install_type_parses_from_its_contract_string_case_sensitively()
    {
        // The exact mapping ingestion performs, so a rename on either side fails here.
        foreach (var contractValue in InventoryChromeExtension.InstallTypes)
        {
            Enum.TryParse<ChromeExtensionInstallType>(contractValue, ignoreCase: false, out var parsed).ShouldBeTrue();
            parsed.ToString().ShouldBe(contractValue);
        }
    }

    [Fact]
    public void The_report_statuses_and_the_domain_statuses_are_the_same_set()
    {
        Enum.GetNames<ChromeReportStatus>().ShouldBe(InventoryChrome.Statuses, ignoreOrder: true);

        foreach (var contractValue in InventoryChrome.Statuses)
        {
            Enum.TryParse<ChromeReportStatus>(contractValue, ignoreCase: false, out var parsed).ShouldBeTrue();
            parsed.ToString().ShouldBe(contractValue);
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("abcdefghijklmnopabcdefghijklmnop", true)]
    [InlineData("abcdefghijklmnopabcdefghijklmno", false)]
    [InlineData("abcdefghijklmnopabcdefghijklmnopa", false)]
    [InlineData("abcdefghijklmnopabcdefghijklmnoq", false)]
    [InlineData("ABCDEFGHIJKLMNOPABCDEFGHIJKLMNOP", false)]
    [InlineData("abcdefghijklmnopabcdefghijklmno1", false)]
    public void The_domain_and_the_contract_agree_on_what_an_extension_id_is(string? value, bool expected)
    {
        InventoryChromeExtension.IsValidExtensionId(value).ShouldBe(expected);
        ChromeExtension.IsValidExtensionId(value).ShouldBe(expected);
    }
}
