using System.Windows.Forms;
using UTscan.Services;

namespace UTscan.UI.Forms;

/// <summary>
/// 登录窗体
/// </summary>
public class LoginForm : Form
{
    private readonly AuthService _auth;
    private TextBox _txtUser = null!;
    private TextBox _txtPass = null!;
    private Button _btnLogin = null!;
    private Button _btnCancel = null!;
    private Label _lblHint = null!;

    public LoginForm(AuthService auth)
    {
        _auth = auth;
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        Text = "登录 — 超声显微扫查系统";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new System.Drawing.Size(360, 200);
        Font = new System.Drawing.Font("Microsoft YaHei UI", 9F);

        var lblUser = new Label { Text = "用户名：", Left = 30, Top = 30, Width = 70, TextAlign = System.Drawing.ContentAlignment.MiddleRight };
        var lblPass = new Label { Text = "密  码：", Left = 30, Top = 70, Width = 70, TextAlign = System.Drawing.ContentAlignment.MiddleRight };

        _txtUser = new TextBox { Left = 110, Top = 28, Width = 210 };
        _txtPass = new TextBox { Left = 110, Top = 68, Width = 210, UseSystemPasswordChar = true };

        _btnLogin = new Button { Text = "登录", Left = 110, Top = 115, Width = 95, Height = 32 };
        _btnLogin.Click += BtnLogin_Click;

        _btnCancel = new Button { Text = "取消", Left = 225, Top = 115, Width = 95, Height = 32 };
        _btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        _lblHint = new Label
        {
            // P0-4：不得在界面上明示密码；账号名可公开，密码由管理员线下分发
            Text = "默认账号：admin（管理员）/ operator（操作员），密码请向管理员索取",
            Left = 20,
            Top = 160,
            Width = 320,
            ForeColor = System.Drawing.Color.Gray,
            Font = new System.Drawing.Font("Microsoft YaHei UI", 7.5F)
        };

        AcceptButton = _btnLogin;
        CancelButton = _btnCancel;

        Controls.Add(lblUser);
        Controls.Add(lblPass);
        Controls.Add(_txtUser);
        Controls.Add(_txtPass);
        Controls.Add(_btnLogin);
        Controls.Add(_btnCancel);
        Controls.Add(_lblHint);
    }

    private void BtnLogin_Click(object? sender, EventArgs e)
    {
        var user = _auth.Login(_txtUser.Text, _txtPass.Text);
        if (user != null)
        {
            DialogResult = DialogResult.OK;
            Close();
        }
        else
        {
            MessageBox.Show("用户名或密码错误。", "登录失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _txtUser.Focus();
        }
    }
}
