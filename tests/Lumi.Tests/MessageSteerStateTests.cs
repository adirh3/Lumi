using System.Collections.Generic;
using Lumi.Localization;
using Lumi.Models;
using Lumi.ViewModels;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Unit tests for the transient steer-delivery badge on <see cref="ChatMessageViewModel"/>. The badge is
/// what makes it clear whether a message typed while a turn was running actually got injected into that
/// running turn (steered), is still in flight, or failed to land.
/// </summary>
public sealed class MessageSteerStateTests
{
    private static ChatMessageViewModel NewUserMessage() =>
        new(new ChatMessage { Role = "user", Content = "hello" });

    [Fact]
    public void DefaultState_HasNoBadge()
    {
        var vm = NewUserMessage();

        Assert.Equal(MessageSteerState.None, vm.SteerState);
        Assert.False(vm.HasSteerBadge);
        Assert.False(vm.IsSteerInProgress);
        Assert.False(vm.IsSteerDelivered);
        Assert.False(vm.IsSteerFailed);
        Assert.False(vm.ShowSteerDot);
        Assert.Equal(string.Empty, vm.SteerBadgeText);
    }

    [Fact]
    public void Steering_ShowsInProgressBadge()
    {
        var vm = NewUserMessage();
        vm.SteerState = MessageSteerState.Steering;

        Assert.True(vm.HasSteerBadge);
        Assert.True(vm.IsSteerInProgress);
        Assert.False(vm.IsSteerDelivered);
        Assert.False(vm.IsSteerFailed);
        // In-flight steer shows the pulsing dot, not the delivered check.
        Assert.True(vm.ShowSteerDot);
        Assert.Equal(Loc.Steer_Steering, vm.SteerBadgeText);
    }

    [Fact]
    public void Steered_ShowsDeliveredBadge()
    {
        var vm = NewUserMessage();
        vm.SteerState = MessageSteerState.Steered;

        Assert.True(vm.HasSteerBadge);
        Assert.False(vm.IsSteerInProgress);
        Assert.True(vm.IsSteerDelivered);
        Assert.False(vm.IsSteerFailed);
        // Delivered swaps the dot for a check glyph.
        Assert.False(vm.ShowSteerDot);
        Assert.Equal(Loc.Steer_Delivered, vm.SteerBadgeText);
    }

    [Fact]
    public void Failed_ShowsNotDeliveredBadge()
    {
        var vm = NewUserMessage();
        vm.SteerState = MessageSteerState.Failed;

        Assert.True(vm.HasSteerBadge);
        Assert.False(vm.IsSteerInProgress);
        Assert.False(vm.IsSteerDelivered);
        Assert.True(vm.IsSteerFailed);
        // Failed keeps a dot (danger-styled), never the delivered check.
        Assert.True(vm.ShowSteerDot);
        Assert.Equal(Loc.Steer_Failed, vm.SteerBadgeText);
    }

    [Fact]
    public void ChangingState_RaisesNotificationsForComputedProperties()
    {
        var vm = NewUserMessage();
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        vm.SteerState = MessageSteerState.Steering;

        Assert.Contains(nameof(ChatMessageViewModel.SteerState), changed);
        Assert.Contains(nameof(ChatMessageViewModel.HasSteerBadge), changed);
        Assert.Contains(nameof(ChatMessageViewModel.IsSteerInProgress), changed);
        Assert.Contains(nameof(ChatMessageViewModel.IsSteerDelivered), changed);
        Assert.Contains(nameof(ChatMessageViewModel.IsSteerFailed), changed);
        Assert.Contains(nameof(ChatMessageViewModel.ShowSteerDot), changed);
        Assert.Contains(nameof(ChatMessageViewModel.SteerBadgeText), changed);
    }

    [Fact]
    public void SettingState_MirrorsOntoModel()
    {
        var message = new ChatMessage { Role = "user", Content = "hello" };
        var vm = new ChatMessageViewModel(message);

        vm.SteerState = MessageSteerState.Steered;

        // The state must live on the model so it survives VM rebuilds within the session.
        Assert.Equal(MessageSteerState.Steered, message.SteerDelivery);
    }

    [Fact]
    public void RebuiltViewModel_RestoresBadgeFromModel()
    {
        // Simulate a transcript/VM rebuild (reconciliation, stall recovery, remount): the model is
        // reused, a fresh VM is constructed from it, and the steer badge must come back.
        var message = new ChatMessage { Role = "user", Content = "hello" };
        var original = new ChatMessageViewModel(message) { SteerState = MessageSteerState.Steered };

        var rebuilt = new ChatMessageViewModel(message);

        Assert.Equal(MessageSteerState.Steered, rebuilt.SteerState);
        Assert.True(rebuilt.HasSteerBadge);
        Assert.Equal(Loc.Steer_Delivered, rebuilt.SteerBadgeText);
    }
}
