using System.Security.Cryptography;
using System.Text;
using UTscan.Core.Enums;
using UTscan.Core.Models;

namespace UTscan.Services;

/// <summary>
/// 认证服务
/// </summary>
/// <remarks>
/// P0-4 整改：凭据不再以明文出现在源码中——密码以 PBKDF2-SHA256（100k 迭代，16 字节盐）哈希存储，
/// 验证使用常数时间比较（防时序侧信道）；LoginAsync 与 Login 统一走同一校验路径。
/// 哈希常量由部署方按需重新生成（换密码即换哈希），源码中无原始密码。
/// </remarks>
public class AuthService
{
    private const int Pbkdf2Iterations = 100_000;
    private const int HashByteLength = 32;

    private static readonly Dictionary<string, (string SaltB64, string HashB64, string DisplayName, UserRole Role)> Users = new()
    {
        ["operator"] = ("U0MAv79jNU8H0pDzUnLwbA==", "thFudR2OVz2QFVCD9PXbpK2RT8B4S4O7KVepJGSAhFs=", "操作员", UserRole.Operator),
        ["admin"] = ("pT7IKJVeV+rmRYeCKF5Ggw==", "JE5Xh1eESmVWBH27tk2G5QWeJFsqe1TpTxs7MZLFi8I=", "管理员", UserRole.Admin)
    };

    private UserProfile? _currentUser;

    /// <summary>当前用户</summary>
    public UserProfile? CurrentUser => _currentUser;

    /// <summary>是否已登录</summary>
    public bool IsLoggedIn => _currentUser?.IsAuthenticated ?? false;

    /// <summary>
    /// 登录验证（按用户名 + PBKDF2 密码哈希精确校验）
    /// </summary>
    public UserProfile? Login(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            return null;

        if (!Users.TryGetValue(username.Trim().ToLowerInvariant(), out var user))
            return null;

        if (!VerifyPassword(password, user.SaltB64, user.HashB64))
            return null;

        _currentUser = new UserProfile
        {
            Username = username.Trim(),
            DisplayName = user.DisplayName,
            Role = user.Role,
            IsAuthenticated = true
        };
        return _currentUser;
    }

    /// <summary>
    /// 异步登录验证（与 <see cref="Login"/> 同一校验路径，供测试/自动化流程使用）
    /// </summary>
    public Task<bool> LoginAsync(string username, string password) =>
        Task.FromResult(Login(username, password) is not null);

    /// <summary>
    /// 登出
    /// </summary>
    public void Logout()
    {
        _currentUser = null;
    }

    /// <summary>PBKDF2-SHA256 常数时间密码校验</summary>
    private static bool VerifyPassword(string password, string saltB64, string expectedHashB64)
    {
        try
        {
            using var pbkdf2 = new Rfc2898DeriveBytes(
                Encoding.UTF8.GetBytes(password),
                Convert.FromBase64String(saltB64),
                Pbkdf2Iterations,
                HashAlgorithmName.SHA256);
            byte[] computed = pbkdf2.GetBytes(HashByteLength);
            return CryptographicOperations.FixedTimeEquals(computed, Convert.FromBase64String(expectedHashB64));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
