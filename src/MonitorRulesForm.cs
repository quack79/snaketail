using System.Windows.Forms;

namespace SnakeTail
{
    public class MonitorRulesForm : Form
    {
        public MonitorRulesForm()
        {
            InitializeComponent();
            Theme.Apply(this);
        }

        private void InitializeComponent()
        {
            this.Text = "Monitor Rules";
            this.Width = 800;
            this.Height = 600;
        }
    }
}
