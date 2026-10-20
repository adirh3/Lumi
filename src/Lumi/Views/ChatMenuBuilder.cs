using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Lumi.Converters;
using Lumi.Localization;
using Lumi.Models;
using Lumi.ViewModels;

namespace Lumi.Views;

/// <summary>
/// Builds the chat actions shared by the sidebar's chat menu and the chat header's title menu, so
/// both offer the same pin state, tags and project targets. Dynamic lists are rebuilt each time a
/// menu opens, so they always reflect the live tags and projects.
/// </summary>
internal static class ChatMenuBuilder
{
    /// <summary>Names the pin action the chat's state allows and points it at the chat.</summary>
    public static void UpdatePinItem(MenuItem pinItem, Chat chat, Func<string, Geometry?> icons)
    {
        pinItem.Header = chat.IsPinned ? Loc.Menu_Unpin : Loc.Menu_Pin;
        pinItem.CommandParameter = chat;
        if (pinItem.Icon is PathIcon icon)
            icon.Data = icons(chat.IsPinned ? "Icon.PinOff" : "Icon.Pin");
    }

    /// <summary>Lists "No tag", every tag (checking the chat's own) and the tag manager.</summary>
    public static void PopulateTagMenu(MenuItem tagMenu, MainViewModel vm, Chat chat, Func<string, Geometry?> icons)
    {
        tagMenu.Items.Clear();
        tagMenu.Items.Add(new MenuItem
        {
            Header = Loc.ChatTags_NoTag,
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = chat.TagId is null,
            Command = vm.ChatTagsVM.AssignTagCommand,
            CommandParameter = new ChatTagAssignment(chat, null)
        });

        var tags = vm.DataStore.Data.ChatTags
            .OrderBy(tag => tag.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (tags.Length > 0)
            tagMenu.Items.Add(new Separator());

        foreach (var tag in tags)
        {
            tagMenu.Items.Add(new MenuItem
            {
                Header = tag.Name,
                Icon = new Border
                {
                    Width = 10,
                    Height = 10,
                    CornerRadius = new CornerRadius(5),
                    Background = ChatTagBrushConverter.CreateBrush(tag.Color)
                },
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = chat.TagId == tag.Id,
                Command = vm.ChatTagsVM.AssignTagCommand,
                CommandParameter = new ChatTagAssignment(chat, tag)
            });
        }

        tagMenu.Items.Add(new Separator());
        tagMenu.Items.Add(new MenuItem
        {
            Header = Loc.ChatTags_Manage,
            Icon = CreateGlyph(icons, "Icon.Gear"),
            Command = vm.ChatTagsVM.OpenManagerCommand
        });
    }

    /// <summary>Lists "All projects" and every project as move targets, checking the chat's current home.</summary>
    public static void PopulateMoveToProjectMenu(MenuItem moveMenu, MainViewModel vm, Chat chat, Func<string, Geometry?> icons)
    {
        moveMenu.Items.Clear();

        var targets = ChatMoveTargetBuilder.Build(vm.Projects, chat, Loc.ProjectSwitcher_AllProjects);
        var separatorAdded = false;

        foreach (var target in targets)
        {
            // Divide the "All projects" pool from the concrete project list.
            if (target.Kind == ChatMoveTargetKind.Project && !separatorAdded)
            {
                moveMenu.Items.Add(new Separator());
                separatorAdded = true;
            }

            moveMenu.Items.Add(CreateMoveTargetMenuItem(target, vm, chat, icons));
        }
    }

    /// <summary>Materializes a single <see cref="ChatMoveTarget"/> into a menu item with the right glyph and action.</summary>
    private static MenuItem CreateMoveTargetMenuItem(ChatMoveTarget target, MainViewModel vm, Chat chat, Func<string, Geometry?> icons)
    {
        var item = new MenuItem { Header = target.Header };

        // The chat's current home is shown as a disabled checkmark rather than an actionable move.
        if (target.IsCurrent)
        {
            item.ToggleType = MenuItemToggleType.CheckBox;
            item.IsChecked = true;
            item.IsEnabled = false;
            return item;
        }

        if (target.Kind == ChatMoveTargetKind.AllProjects)
        {
            item.Icon = CreateGlyph(icons, "Icon.Browse");
            item.Click += (_, _) => vm.RemoveChatFromProjectCommand.Execute(chat);
            return item;
        }

        var project = vm.Projects.FirstOrDefault(p => p.Id == target.ProjectId);
        if (project is not null)
        {
            item.Icon = CreateGlyph(icons, "Icon.Folder");
            item.Click += (_, _) => vm.AssignChatToProjectCommand.Execute(new object[] { chat, project });
        }

        return item;
    }

    private static PathIcon? CreateGlyph(Func<string, Geometry?> icons, string resourceKey)
    {
        var geometry = icons(resourceKey);
        return geometry is null ? null : new PathIcon { Data = geometry, Width = 14, Height = 14 };
    }
}
