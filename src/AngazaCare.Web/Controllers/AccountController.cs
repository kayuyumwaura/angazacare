using System.Security.Claims;
using AngazaCare.Data;
using AngazaCare.Domain;
using AngazaCare.Models;
using AngazaCare.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AngazaCare.Controllers;

public abstract class AppController : Controller
{
    protected int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    protected string Role => User.FindFirstValue(ClaimTypes.Role)!;
    protected Actor Actor => new(UserId, Role);

    protected IActionResult Done(string message, string action, string? controller = null, object? routeValues = null)
    {
        TempData["Toast"] = message;
        return RedirectToAction(action, controller, routeValues);
    }
}

public sealed class AccountController(UserRepo users) : AppController
{
    [HttpGet, AllowAnonymous]
    public IActionResult Login(string? returnUrl)
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToAction("Index", "Home");
        return View(new LoginInput { ReturnUrl = returnUrl });
    }

    [HttpPost, AllowAnonymous]
    public async Task<IActionResult> Login(LoginInput input)
    {
        if (!ModelState.IsValid) return View(input);
        var phone = PhoneProtector.Normalize(input.Phone);
        var user = phone is null ? null : await users.ByPhoneAsync(phone);
        if (user is null || !user.Active || !PasswordHasher.Verify(input.Password, user.PasswordHash))
        {
            ModelState.AddModelError("", "That phone number and password do not match an active account.");
            return View(input);
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.FullName),
            new Claim(ClaimTypes.Role, user.Role),
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });

        return Url.IsLocalUrl(input.ReturnUrl) ? Redirect(input.ReturnUrl!) : RedirectToAction("Index", "Home");
    }

    [HttpPost]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    [HttpGet, AllowAnonymous]
    public IActionResult Denied() => View();
}

[Authorize]
public sealed class HomeController : AppController
{
    public IActionResult Index() => Role == Roles.Chw ? RedirectToAction("Index", "Chw") : RedirectToAction("Index", "Nurse");

    [AllowAnonymous] public IActionResult Offline() => View();

    [AllowAnonymous, Route("/error")] public IActionResult Error() => View();
}
