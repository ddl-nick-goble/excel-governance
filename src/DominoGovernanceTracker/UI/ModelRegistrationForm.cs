using System;
using System.Drawing;
using System.Windows.Forms;

namespace DominoGovernanceTracker.UI
{
    /// <summary>
    /// Modal dialog for registering or re-registering a workbook model.
    /// </summary>
    public class ModelRegistrationForm : Form
    {
        private TextBox _modelNameTextBox;
        private TextBox _descriptionTextBox;
        private Label _versionInfoLabel;
        private Button _registerButton;
        private Button _cancelButton;

        private readonly bool _isReregister;

        /// <summary>
        /// The model name entered by the user.
        /// </summary>
        public string ModelName => _modelNameTextBox.Text.Trim();

        /// <summary>
        /// The description entered by the user.
        /// </summary>
        public string Description => _descriptionTextBox.Text.Trim();

        /// <summary>
        /// Creates a new registration form.
        /// </summary>
        /// <param name="existingModelName">If re-registering, the locked model name. Null for new registration.</param>
        /// <param name="existingVersion">Current version number if re-registering.</param>
        /// <param name="existingDescription">Current description if re-registering.</param>
        public ModelRegistrationForm(
            string existingModelName = null,
            int existingVersion = 0,
            string existingDescription = null)
        {
            _isReregister = !string.IsNullOrEmpty(existingModelName);
            InitializeComponents(existingModelName, existingVersion, existingDescription);
        }

        private void InitializeComponents(string existingModelName, int existingVersion, string existingDescription)
        {
            Text = _isReregister ? "Re-register Model (New Version)" : "Register Workbook Model";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            Size = new Size(500, 420);
            MinimumSize = new Size(400, 350);
            Padding = new Padding(20);

            var tableLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 6,
                Padding = new Padding(4),
            };
            tableLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tableLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Model Name label
            tableLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); // Model Name input
            tableLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Description label
            tableLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100)); // Description input
            tableLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Version info
            tableLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Buttons

            // Model Name
            var nameLabel = new Label
            {
                Text = "Model Name:",
                AutoSize = true,
                Font = new Font(Font.FontFamily, 9.5f),
                Margin = new Padding(0, 4, 0, 4),
            };
            tableLayout.Controls.Add(nameLabel, 0, 0);

            _modelNameTextBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font(Font.FontFamily, 10f),
                Margin = new Padding(0, 0, 0, 8),
            };
            if (_isReregister)
            {
                _modelNameTextBox.Text = existingModelName;
                _modelNameTextBox.ReadOnly = true;
                _modelNameTextBox.BackColor = SystemColors.Control;
            }
            tableLayout.Controls.Add(_modelNameTextBox, 0, 1);

            // Description
            var descLabel = new Label
            {
                Text = "Description:",
                AutoSize = true,
                Font = new Font(Font.FontFamily, 9.5f),
                Margin = new Padding(0, 4, 0, 4),
            };
            tableLayout.Controls.Add(descLabel, 0, 2);

            _descriptionTextBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                AcceptsReturn = true,
                WordWrap = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font(Font.FontFamily, 10f),
                Margin = new Padding(0, 0, 0, 8),
                Text = existingDescription ?? "",
            };
            tableLayout.Controls.Add(_descriptionTextBox, 0, 3);

            // Version info (only shown on re-register)
            _versionInfoLabel = new Label
            {
                AutoSize = true,
                Margin = new Padding(0, 4, 0, 8),
                ForeColor = SystemColors.GrayText,
            };
            if (_isReregister)
            {
                _versionInfoLabel.Text = $"Current version: {existingVersion}  →  New version: {existingVersion + 1}";
            }
            else
            {
                _versionInfoLabel.Text = "This will create version 1 of the model.";
            }
            tableLayout.Controls.Add(_versionInfoLabel, 0, 4);

            // Buttons
            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(0, 8, 0, 4),
            };

            _cancelButton = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Size = new Size(90, 32),
                Margin = new Padding(8, 0, 0, 0),
            };
            buttonPanel.Controls.Add(_cancelButton);

            _registerButton = new Button
            {
                Text = _isReregister ? "Re-register" : "Register",
                DialogResult = DialogResult.OK,
                Size = new Size(110, 32),
            };
            _registerButton.Click += OnRegisterClick;
            buttonPanel.Controls.Add(_registerButton);

            tableLayout.Controls.Add(buttonPanel, 0, 5);

            Controls.Add(tableLayout);

            AcceptButton = _registerButton;
            CancelButton = _cancelButton;
        }

        private void OnRegisterClick(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_modelNameTextBox.Text))
            {
                MessageBox.Show(
                    "Model name is required.",
                    "Validation",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
            }
        }
    }
}
