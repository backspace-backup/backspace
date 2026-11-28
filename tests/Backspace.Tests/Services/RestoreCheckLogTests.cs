using System.Collections.Generic;
using System.Linq;
using Backspace.Models;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>The owner's restore check of 27 November 2026: names with $ in a
/// restore's selection matched nothing, and the failed check counted 4 files
/// as 8.</summary>
public class RestoreCheckLogTests
{
    [Theory]
    [InlineData("ProgramData/ChocolateyHttpCache/9310b7b8$community.chocolatey.org_api_v2_/list_Packages(Id='KB2919355')_page1.dat",
        "/ProgramData/ChocolateyHttpCache/9310b7b8$$community.chocolatey.org_api_v2_/list_Packages(Id='KB2919355')_page1.dat")]
    [InlineData("a$$b ${HOME} $-1 $", "/a$$$$b $${HOME} $$-1 $$")]
    [InlineData("Photos [2026]/x].txt", "/Photos [[]2026]/x].txt")]
    [InlineData("odd*name?.txt", "/odd[*]name[?].txt")]
    [InlineData("ends with space ", "/ends with space[ ]")]
    [InlineData("two  spaces\t ", "/two  spaces[\t][ ]")]
    [InlineData("plain/inner space.txt", "/plain/inner space.txt")]
    public void An_include_line_names_exactly_its_path(string path, string line)
        => Assert.Equal(line, ResticEngine.ResticIncludeLine(path));

    private static FileVerifyResult File(string path, FileVerifyStatus status) => new(path, 1, status == FileVerifyStatus.OK ? 1 : 0, status, null);
    private static FileVerifyResult Operation() => new(BackupVerifier.OperationRow, 0, 0, FileVerifyStatus.RestoreFailed,
        "Recovered 1023 of 1024 requested files. 1 item(s) could not be restored.");

    [Fact]
    public void Four_files_not_restored_in_four_batches_read_four_files_not_eight()
    {
        var results = Enumerable.Range(0, 6000).Select(i => File($"ok{i}", FileVerifyStatus.OK)).ToList();
        for (var i = 0; i < 4; i++) { results.Add(File($"missing{i}", FileVerifyStatus.RestoreFailed)); results.Add(Operation()); }
        Assert.Equal("«G»: rev 25 · 4 files could not be recovered correctly. 0 additional files differ from today's originals.",
            BackupVerifier.FailureLine("G", "rev 25", results));
        results.RemoveAll(r => r.Path is "missing1" or "missing2" or "missing3");
        Assert.Contains("· 1 file could not be recovered correctly.", BackupVerifier.FailureLine("G", "rev 25", results));
    }

    [Fact]
    public void Differences_alone_and_a_restore_problem_alone_say_so()
    {
        var differ = new List<FileVerifyResult> { File("a", FileVerifyStatus.OK), File("b", FileVerifyStatus.BytesMismatch) };
        Assert.Equal("«G»: rev 3 · Files recovered successfully, but 1 differ from today's originals. This does not establish which copy changed.",
            BackupVerifier.FailureLine("G", "rev 3", differ));
        var problem = new List<FileVerifyResult> { File("a", FileVerifyStatus.OK), Operation() };
        Assert.Equal("«G»: rev 3 · Every file was recovered, but the restore reported a problem: Recovered 1023 of 1024 requested files. 1 item(s) could not be restored.",
            BackupVerifier.FailureLine("G", "rev 3", problem));
    }
}
