using Meows.Bot;
using Meows.Plugins.Kibble.ViewModels;

namespace Meows.Tests;

/// <summary>
/// Getting back out of a group's Held_Back.
///
/// Opening one replaces the folder being sorted, it happens from a small button on a destination
/// card, and every group's is called Held_Back, so before this there was no way back and no way
/// to tell which one you had landed in.
/// </summary>
public sealed class KibbleBackTests
{
    private GroupConfig _alpha = null!;
    private GroupConfig _beta = null!;

    private (KibbleViewModel Model, TempWorkspace Temp, string Folder) Open(params string[] names)
    {
        var temp = new TempWorkspace();
        _alpha = temp.AddGroup("Alpha");
        _beta = temp.AddGroup("Beta", "-100222");
        temp.WriteConfig(_alpha, _beta);

        var folder = Path.Combine(temp.Root, "intake");
        Directory.CreateDirectory(folder);
        foreach (var name in names)
            File.WriteAllBytes(Path.Combine(folder, name), System.Text.Encoding.UTF8.GetBytes(name));

        var model = new KibbleViewModel(new FakeHost(Path.Combine(temp.Root, "hostdata")));
        model.SetBotRoot(temp.Workspace.Root);
        model.LoadFolder(folder);
        return (model, temp, folder);
    }

    private string Held(TempWorkspace temp, string group)
    {
        var folder = temp.Workspace.HeldBackFolder(group == "Alpha" ? _alpha : _beta);
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public void There_is_nowhere_to_go_back_to_until_somewhere_has_been_left()
    {
        var (model, temp, _) = Open("a.png");
        using var _t = temp;

        Assert.False(model.CanGoBack);
        Assert.False(model.BackCommand.CanExecute(null));
    }

    [Fact]
    public void Opening_a_held_back_folder_leaves_a_way_back_to_where_you_were()
    {
        var (model, temp, folder) = Open("a.png");
        using var _t = temp;

        model.LoadFolder(Held(temp, "Alpha"));

        Assert.True(model.CanGoBack);
        Assert.True(model.IsInHeldBack);
        // The button says where it goes, by the folder's own name.
        Assert.Contains("intake", model.BackLabel);

        model.BackCommand.Execute(null);

        Assert.Equal(folder, model.SourceFolder);
        Assert.False(model.IsInHeldBack);
        Assert.False(model.CanGoBack);
    }

    [Fact]
    public void A_held_back_folder_is_named_for_its_group_rather_than_for_itself()
    {
        // Every one of them is a folder called Held_Back, so the name on its own says nothing.
        var (model, temp, _) = Open("a.png");
        using var _t = temp;

        model.LoadFolder(Held(temp, "Beta"));

        Assert.Contains("Beta", model.HeldBackText);
        Assert.Contains("Beta", model.StatusMessage);
    }

    [Fact]
    public void Refreshing_is_not_somewhere_to_go_back_from()
    {
        var (model, temp, _) = Open("a.png");
        using var _t = temp;

        model.RefreshCommand.Execute(null);
        model.RefreshCommand.Execute(null);

        Assert.False(model.CanGoBack);
    }

    [Fact]
    public void Two_hops_come_back_one_at_a_time()
    {
        var (model, temp, folder) = Open("a.png");
        using var _t = temp;
        var alpha = Held(temp, "Alpha");
        var beta = Held(temp, "Beta");

        model.LoadFolder(alpha);
        model.LoadFolder(beta);

        model.BackCommand.Execute(null);
        Assert.Equal(alpha, model.SourceFolder);

        model.BackCommand.Execute(null);
        Assert.Equal(folder, model.SourceFolder);
        Assert.False(model.CanGoBack);
    }

    [Fact]
    public void Going_back_does_not_put_the_folder_it_left_on_the_stack()
    {
        // Otherwise Back bounces between the last two folders for ever instead of walking out.
        var (model, temp, folder) = Open("a.png");
        using var _t = temp;

        model.LoadFolder(Held(temp, "Alpha"));
        model.BackCommand.Execute(null);

        Assert.Equal(folder, model.SourceFolder);
        Assert.False(model.CanGoBack);
    }

    [Fact]
    public void A_folder_that_has_gone_since_it_was_left_says_so_rather_than_loading_nothing()
    {
        var (model, temp, folder) = Open("a.png");
        using var _t = temp;

        model.LoadFolder(Held(temp, "Alpha"));
        Directory.Delete(folder, recursive: true);

        model.BackCommand.Execute(null);

        Assert.NotNull(model.ErrorMessage);
        // Still where it was, and the stack has let go of the folder that is gone.
        Assert.False(model.CanGoBack);
    }
}
