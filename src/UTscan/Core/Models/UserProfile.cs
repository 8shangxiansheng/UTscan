using UTscan.Core.Enums;

namespace UTscan.Core.Models;

/// <summary>
/// 用户档案
/// </summary>
public class UserProfile
{
    /// <summary>用户名</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>显示名称</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>用户角色</summary>
    public UserRole Role { get; set; } = UserRole.Guest;

    /// <summary>是否已认证</summary>
    public bool IsAuthenticated { get; set; }
}
