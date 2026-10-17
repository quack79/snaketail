using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Xunit;

namespace SnakeTail.Tests;

public class TailFormDrawTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Drawing_a_line_should_not_invalidate_the_list_view(bool selected)
    {
        // Invalidating from inside the paint handler makes the ListView repaint forever (CPU spin after opening a file).
        int invalidateCount = RunOnStaThread(() =>
        {
            using var form = new TailForm();
            var listView = (ListView)typeof(TailForm).GetField("_tailListView", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
            listView.VirtualMode = false;
            _ = listView.Handle;

            var item = new ListViewItem("raw line");
            item.SubItems.Add("display line");
            listView.Items.Add(item);
            item.Selected = selected;

            int count = 0;
            listView.Invalidated += (s, e) => count++;

            using var bitmap = new Bitmap(200, 20);
            using var graphics = Graphics.FromImage(bitmap);
            var args = new DrawListViewSubItemEventArgs(graphics, new Rectangle(0, 0, 200, 20), item, item.SubItems[1], 0, 1, listView.Columns[1], selected ? ListViewItemStates.Selected : ListViewItemStates.Default);
            typeof(TailForm).GetMethod("_tailListView_DrawSubItem", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, new object[] { listView, args });
            return count;
        });

        Assert.Equal(0, invalidateCount);
    }

    private static T RunOnStaThread<T>(Func<T> action)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null)
            throw new Xunit.Sdk.XunitException("STA test action failed: " + error);
        return result;
    }
}
