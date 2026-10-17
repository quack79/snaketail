using System.Windows.Forms;

namespace SnakeTail
{
    // Lightweight editor form for a single rule. This is a placeholder for MVP.
    public partial class MonitorRuleEditForm : Form
    {
        public MonitorRuleEditForm()
        {
            InitializeComponent();
            Theme.Apply(this);
        }

        private void InitializeComponent()
        {
            this.Text = "Edit Monitor Rule";
            this.Width = 480;
            this.Height = 320;
        }
    }
}
