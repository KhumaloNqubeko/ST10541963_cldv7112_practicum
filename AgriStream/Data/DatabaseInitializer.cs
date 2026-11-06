using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
namespace AgriStream.Data;
public static class DatabaseInitializer {
    public static async Task InitializeAsync(IServiceProvider services, IConfiguration config) {
        var username = config["BootstrapAdmin:Username"];
        var password = config["BootstrapAdmin:Password"];
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) throw new InvalidOperationException("Set BootstrapAdmin:Username and BootstrapAdmin:Password using secrets or environment variables.");
        await services.GetRequiredService<ApplicationDbContext>().Database.MigrateAsync();
        var roles = services.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in SystemRoles.All) if (!await roles.RoleExistsAsync(role)) Check(await roles.CreateAsync(new IdentityRole(role)));
        var users = services.GetRequiredService<UserManager<IdentityUser>>();
        var user = await users.FindByNameAsync(username);
        if (user is null) { user = new IdentityUser { UserName = username, Email = username }; Check(await users.CreateAsync(user, password)); Check(await users.AddToRoleAsync(user, SystemRoles.Admin)); }
        else if (!await users.IsInRoleAsync(user, SystemRoles.Admin)) throw new InvalidOperationException("Bootstrap username belongs to a non-admin account. Choose a new username.");
    }
    static void Check(IdentityResult r) { if (!r.Succeeded) throw new InvalidOperationException(string.Join("; ", r.Errors.Select(e => e.Description))); }
}
