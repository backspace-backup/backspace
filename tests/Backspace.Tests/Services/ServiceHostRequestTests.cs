using System;
using System.Text;
using Backspace.Services.Platform.Windows;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class ServiceHostRequestTests
{
    [Theory]
    [InlineData("{\"version\":1,\"command\":\"status\"}")]
    [InlineData("{\"version\":1,\"command\":\"cancel\",\"runId\":\"0123456789abcdef0123456789abcdef\"}")]
    [InlineData("{\"version\":1,\"command\":\"heartbeat\",\"runId\":\"0123456789abcdef0123456789abcdef\"}")]
    [InlineData("{\"version\":1,\"command\":\"start-cli\",\"revision\":\"0123456789abcdef0123456789abcdef\",\"backupId\":\"backup\",\"operation\":\"check\"}")]
    [InlineData("{\"version\":1,\"command\":\"start\",\"revision\":\"0123456789abcdef0123456789abcdef\",\"backupId\":\"backup\",\"operation\":\"backup\"}")]
    public void Only_the_bounded_control_vocabulary_is_accepted(string json) =>
        Assert.NotNull(ServiceHostRequest.Parse(Encoding.UTF8.GetBytes(json)));

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"version\":2,\"command\":\"status\"}")]
    [InlineData("{\"version\":1,\"command\":\"status\",\"command\":\"status\"}")]
    [InlineData("{\"version\":1,\"command\":\"status\",\"executable\":\"evil.exe\"}")]
    [InlineData("{\"version\":1,\"command\":\"status\",\"runId\":\"0123456789abcdef0123456789abcdef\"}")]
    [InlineData("{\"version\":1,\"command\":\"cancel\"}")]
    [InlineData("{\"version\":1,\"command\":\"heartbeat\"}")]
    [InlineData("{\"version\":1,\"command\":\"heartbeat\",\"runId\":\"0123456789abcdef0123456789abcdef\",\"operation\":\"backup\"}")]
    [InlineData("{\"version\":1,\"command\":\"start-scheduled\",\"revision\":\"stale\",\"backupId\":\"backup\",\"operation\":\"backup\"}")]
    [InlineData("{\"version\":1,\"command\":\"start\",\"revision\":\"stale\",\"backupId\":\"backup\",\"operation\":\"backup\"}")]
    [InlineData("{\"version\":1,\"command\":\"start\",\"revision\":\"0123456789abcdef0123456789abcdef\",\"backupId\":\"backup\",\"operation\":\"restore\"}")]
    [InlineData("{\"version\":1,\"command\":\"start\",\"revision\":\"0123456789abcdef0123456789abcdef\",\"backupId\":\" \",\"operation\":\"backup\"}")]
    // The service keeps the schedule itself (U043): no client may start a scheduled run.
    [InlineData("{\"version\":1,\"command\":\"start-scheduled\",\"revision\":\"0123456789abcdef0123456789abcdef\",\"backupId\":\"backup\",\"operation\":\"check\"}")]
    [InlineData("{\"version\":1,\"command\":\"scheduled\",\"revision\":\"0123456789abcdef0123456789abcdef\",\"backupId\":\"backup\",\"operation\":\"check\"}")]
    public void Ambiguous_unapproved_and_injected_commands_fail(string json) =>
        Assert.NotNull(Record.Exception(() => ServiceHostRequest.Parse(Encoding.UTF8.GetBytes(json))));

    [Fact]
    public void Oversized_and_empty_messages_fail()
    {
        Assert.Throws<System.IO.InvalidDataException>(() => ServiceHostRequest.Parse(new byte[4097]));
        Assert.Throws<System.IO.InvalidDataException>(() => ServiceHostRequest.Parse(Array.Empty<byte>()));
    }
}
