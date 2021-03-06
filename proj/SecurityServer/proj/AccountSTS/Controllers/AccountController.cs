﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading.Tasks;
using System.Web;
using System.Web.Configuration;
using System.Web.Mvc;
using System.Web.Routing;
using Dragon.SecurityServer.AccountSTS.ActionFilters;
using Dragon.SecurityServer.AccountSTS.App_Start;
using Dragon.SecurityServer.AccountSTS.Helpers;
using Dragon.SecurityServer.AccountSTS.Models;
using Dragon.SecurityServer.AccountSTS.Services;
using Dragon.SecurityServer.AccountSTS.Services.CheckPasswortServices;
using Dragon.SecurityServer.Common;
using Dragon.SecurityServer.Identity.Stores;
using Microsoft.AspNet.Identity;
using Microsoft.AspNet.Identity.Owin;
using Microsoft.Owin.Security;
using NLog;
using System.Transactions;
using Dragon.Data.Interfaces;

namespace Dragon.SecurityServer.AccountSTS.Controllers
{
    [Authorize]
    public class AccountController : Controller
    {
        private const string LegacyPasswordPrefix = "OLD_";
        private const string SignIn = "wsignin1.0";

        [Import]
        public ICheckPasswordService<AppMember> LegacyPasswordService { get; set; }

        private readonly IDragonUserStore<AppMember> _userStore;
        private ApplicationSignInManager _signInManager;
        private ApplicationUserManager _userManager;
        private readonly IFederationService _federationService;
        private readonly IAppService _appService;
        private readonly IRepository<UserActivity> _userActivityRepository;

        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public AccountController(ApplicationUserManager userManager, ApplicationSignInManager signInManager, IDragonUserStore<AppMember> userStore, IFederationService federationService, IAppService appService, IRepository<UserActivity> userActivityRepository)
        {
            _userStore = userStore;
            _federationService = federationService;
            _appService = appService;
            _userActivityRepository = userActivityRepository;
            UserManager = userManager;
            SignInManager = signInManager;
        }

        protected override void Initialize(RequestContext requestContext)
        {
            base.Initialize(requestContext);
            // keep the federation context
            var routeValues = new RouteValueDictionary();
            if (Request != null && Request.QueryString != null)
            {
                foreach (var key in Request.QueryString.AllKeys)
                {
                    routeValues.Add(key, Request.QueryString.Get(key));
                }
            }
            ViewBag.RouteValues = routeValues;
        }

        public ApplicationSignInManager SignInManager
        {
            get
            {
                return _signInManager ?? HttpContext.GetOwinContext().Get<ApplicationSignInManager>();
            }
            private set 
            { 
                _signInManager = value; 
            }
        }

        public ApplicationUserManager UserManager
        {
            get
            {
                return _userManager ?? HttpContext.GetOwinContext().GetUserManager<ApplicationUserManager>();
            }
            private set
            {
                _userManager = value;
            }
        }

        //
        // GET: /Account/Login
        [AllowAnonymous, ImportModelStateFromTempData]
        public ActionResult Login(string returnUrl)
        {
            return View();
        }

        //
        // POST: /Account/Login
        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> Login(LoginViewModel model, string returnUrl)
        {
            if (!ModelState.IsValid)
            {
                return View(model);
            }

            // This doesn't count login failures towards account lockout
            // To enable password failures to trigger account lockout, change to shouldLockout: true
            SignInStatus result;
            try
            {
                result = await SignInManager.PasswordSignInAsync(model.Email, model.Password, model.RememberMe, false);
            }
            catch (NotRegisteredForServiceException)
            {
                HandleUserNotRegisteredForService(model.Email);
                return View();
            }
            catch (FormatException)
            {
                // Legacy passwords are not valid Base-64 strings
                result = SignInStatus.Failure;

                // Try to authenticate the user using a legacy login service
                var user = await _userStore.FindByEmailAsync(model.Email);
                if (!string.IsNullOrWhiteSpace(user?.PasswordHash) && user.PasswordHash.StartsWith(LegacyPasswordPrefix) && !string.IsNullOrWhiteSpace(model.Email))
                {
                    if (LegacyPasswordService != null && await LegacyPasswordService.CheckPasswordAsync(user, model.Password))
                    {
                        // set the password
                        using (var tx = new TransactionScope())
                        {
                            _userManager.RemovePassword(user.Id);
                            _userManager.AddPassword(user.Id, model.Password);
                            var updatedUser = await _userStore.FindByIdAsync(user.Id);
                            if (string.IsNullOrEmpty(updatedUser.PasswordHash))
                            {
                                throw new Exception("Unable to set password of user " + user.Id);
                            }
                            tx.Complete();
                        }
                        // and login
                        try
                        {
                            result = await SignInManager.PasswordSignInAsync(model.Email, model.Password, model.RememberMe, false);
                        }
                        catch (NotRegisteredForServiceException)
                        {
                            HandleUserNotRegisteredForService(model.Email);
                            return View();
                        }
                        catch (AppNotAllowedException)
                        {
                            return await SelectAppOrRedirect(model.Email);
                        }
                        // or force a password reset
                        //return await RequestPasswordReset(new ForgotPasswordViewModel { Email = user.Email});
                    }
                }
                else
                {
                    // Not a legacy password, so rethrow
                    throw;
                }
            }
            catch (AppNotAllowedException)
            {
                return await SelectAppOrRedirect(model.Email);
            }

            switch (result)
            {
                case SignInStatus.Success:
                    return RedirectToLocal(returnUrl);
                case SignInStatus.LockedOut:
                    Logger.Trace("Login failed: user {0} is locked out", model.Email);
                    return View("Lockout");
                case SignInStatus.RequiresVerification:
                    Logger.Trace("Login failed: user {0} is unverified", model.Email);
                    return RedirectToAction("SendCode", new { ReturnUrl = returnUrl, RememberMe = model.RememberMe });
                case SignInStatus.Failure:
                default:
                    await HandleLoginFailure(model);
                    return View(model);
            }
        }

        private async Task<ActionResult> SelectAppOrRedirect(string email)
        {
            var user = await _userStore.FindByEmailAsync(email);
            var allowedApps = _appService.GetRegisteredAppsInSameGroup(Guid.Parse(user.Id), Guid.Parse(RequestHelper.GetCurrentAppId()));
            if (allowedApps.Count == 1)
            {
                // no need for the app selection screen, just redirect to the only possible app
                return Redirect(allowedApps.First().Url);
            }
            return RedirectToAction("SelectApp", ViewBag.RouteValues);
        }

        [HttpGet]
        public async Task<ActionResult> SelectApp(string appId)
        {
            var user = await UserManager.FindByIdAsync(User.Identity.GetUserId());
            if (user == null) return new HttpNotFoundResult();
            var allowedApps = _appService.GetRegisteredAppsInSameGroup(Guid.Parse(user.Id), Guid.Parse(appId));
            return View(allowedApps);
        }

        private async Task HandleLoginFailure(LoginViewModel model)
        {
            Logger.Trace("Login failed: user {0}", model.Email);
            ModelState.AddModelError("", await GenerateTryLoginWithFederationProviderErrorMessage(model.Email));
            SignOut();
        }

        private static void SignOut()
        {
            System.Web.HttpContext.Current.GetOwinContext().Authentication.SignOut(DefaultAuthenticationTypes.ApplicationCookie);
            System.Web.HttpContext.Current.Session.Abandon();
        }

        private async Task<string> GenerateTryLoginWithFederationProviderErrorMessage(string email)
        {
            var message = Resources.Global.InvalidLoginAttempt;
            var user = await _userStore.FindByEmailAsync(email);
            if (user != null)
            {
                var loginSuggestionMessage = await GenerateLoginSuggestionMessage(user);
                if (!string.IsNullOrWhiteSpace(loginSuggestionMessage))
                {
                    message += " " + loginSuggestionMessage;
                }
            }
            return message;
        }

        private async Task<string> GenerateLoginSuggestionMessage(AppMember user)
        {
            var availableLogins = await _userStore.GetLoginsAsync(user);
            if (!availableLogins.Any()) return "";
            return string.Format(Resources.Global.LoginUsingOneOf, availableLogins.Count > 1 ? Resources.Global.OneOf + " " : "",
                    availableLogins.Select(x => x.LoginProvider).Aggregate((x1, x2) => $"{x1}, {x2}"));
        }

        //
        // GET: /Account/VerifyCode
        [AllowAnonymous]
        public async Task<ActionResult> VerifyCode(string provider, string returnUrl, bool rememberMe)
        {
            // Require that the user has already logged in via username/password or external login
            if (!await SignInManager.HasBeenVerifiedAsync())
            {
                return View("Error");
            }
            return View(new VerifyCodeViewModel { Provider = provider, ReturnUrl = returnUrl, RememberMe = rememberMe });
        }

        //
        // POST: /Account/VerifyCode
        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> VerifyCode(VerifyCodeViewModel model)
        {
            throw new NotSupportedException();
            if (!ModelState.IsValid)
            {
                return View(model);
            }

            // The following code protects for brute force attacks against the two factor codes. 
            // If a user enters incorrect codes for a specified amount of time then the user account 
            // will be locked out for a specified amount of time. 
            // You can configure the account lockout settings in IdentityConfig
            var result = await SignInManager.TwoFactorSignInAsync(model.Provider, model.Code, isPersistent:  model.RememberMe, rememberBrowser: model.RememberBrowser);
            switch (result)
            {
                case SignInStatus.Success:
                    return RedirectToLocal(model.ReturnUrl);
                case SignInStatus.LockedOut:
                    Logger.Trace("Verify code: locked out (code {0})", model.Code);
                    return View("Lockout");
                case SignInStatus.Failure:
                default:
                    Logger.Trace("Verify code: invalid code {0}", model.Code);
                    ModelState.AddModelError("", "Invalid code.");
                    return View(model);
            }
        }

        //
        // GET: /Account/Register
        [AllowAnonymous]
        public ActionResult Register()
        {
            return View();
        }

        //
        // POST: /Account/Register
        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> Register(RegisterViewModel model)
        {
            if (ModelState.IsValid)
            {
                var user = new AppMember { UserName = model.Email, Email = model.Email };
                var result = await UserManager.CreateAsync(user, model.Password);
                if (result.Succeeded)
                {
                    AddRegisterActivity(user, "Local");
                    //  Comment the following line to prevent log in until the user is confirmed.
                    await SignInManager.SignInAsync(user, isPersistent:false, rememberBrowser:false);
                    
                    // For more information on how to enable account confirmation and password reset please visit http://go.microsoft.com/fwlink/?LinkID=320771
                    // Send an email with this link
                    string code = await UserManager.GenerateEmailConfirmationTokenAsync(user.Id);
                    var callbackUrl = Url.Action("ConfirmEmail", "Account", new { userId = user.Id, code = code }, protocol: Request.Url.Scheme);
                    await UserManager.SendEmailAsync(
                        user.Id,
                        Resources.Global.ConfirmAccountTitle,
                        Resources.Global.ConfirmAccountMessage.Replace("Url", callbackUrl));

                    var returnUrl = Request.QueryString["ReturnUrl"];
                    return string.IsNullOrEmpty(returnUrl) ? RedirectToAction("Index", "Home") : RedirectToLocal(returnUrl);
                }
                var message = $"Register failed: {string.Join("; ", result.Errors)}";
                StackExchange.Exceptional.ErrorStore.LogException(new Exception(message), null);

                // avoid Name x is already taken. errors
                var identityResult = new IdentityResult(result.Errors.Where(
                    x => !x.StartsWith(Resources.Global.Name + " ") && !x.Contains(" " + Resources.Global.Name + " ")));
                AddErrors(identityResult);

                // Show a hint about external providers
                ModelState.AddModelError("", Resources.Global.AlreadyRegisteredUsingXMessage
                    .Replace("{LoginProviders}", WebConfigurationManager.AppSettings["AuthenticationProviders"]
                    .ReplaceLast(",", " " + Resources.Global.Or + " ")));
            }

            // If we got this far, something failed, redisplay form
            return View(model);
        }

        //
        // GET: /Account/ConfirmEmail
        [AllowAnonymous]
        public async Task<ActionResult> ConfirmEmail(string userId, string code)
        {
            if (userId == null || code == null)
            {
                Logger.Trace("Confirm email failed: userId ({0}) or code ({1}) are null", userId, code);
                return View("Error");
            }
            var result = await UserManager.ConfirmEmailAsync(userId, code);
            if (!result.Succeeded)
            {
                  Logger.Trace("Confirm email failed: userId {0}, code {1}", userId, code);
            }
            return View(result.Succeeded ? "ConfirmEmail" : "Error");
        }

        //
        // GET: /Account/ForgotPassword
        [AllowAnonymous]
        public ActionResult ForgotPassword()
        {
            return View();
        }

        //
        // POST: /Account/ForgotPassword
        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> ForgotPassword(ForgotPasswordViewModel model)
        {
            if (ModelState.IsValid)
            {
                return await RequestPasswordReset(model);
            }
            Logger.Trace("Forgot password failed: invalid model state");

            // If we got this far, something failed, redisplay form
            return View(model);
        }

        private async Task<ActionResult> RequestPasswordReset(ForgotPasswordViewModel model)
        {
            var user = await UserManager.FindByNameAsync(model.Email);
            if (user == null)
            {
                Logger.Trace("Forgot password failed: user {0} not found", model.Email);
                // Don't reveal that the user does not exist or is not confirmed
                return View("ForgotPasswordConfirmation");
            }

            // For more information on how to enable account confirmation and password reset please visit http://go.microsoft.com/fwlink/?LinkID=320771
            // Send an email with this link
            string code = await UserManager.GeneratePasswordResetTokenAsync(user.Id);
            var callbackUrl = Url.Action("ResetPassword", "Account", new {userId = user.Id, code = code},
                protocol: Request.Url.Scheme);
            await
                UserManager.SendEmailAsync(
                    user.Id,
                    Resources.Global.ResetPasswordTitle,
                    Resources.Global.ResetPasswordMessage.Replace("Url", callbackUrl));
            return RedirectToAction("ForgotPasswordConfirmation", "Account", ViewBag.RouteValues);
        }

        //
        // GET: /Account/ForgotPasswordConfirmation
        [AllowAnonymous]
        public ActionResult ForgotPasswordConfirmation()
        {
            return View();
        }

        //
        // GET: /Account/ResetPassword
        [AllowAnonymous]
        public ActionResult ResetPassword(string code)
        {
            return code == null ? View("Error") : View();
        }

        //
        // POST: /Account/ResetPassword
        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> ResetPassword(ResetPasswordViewModel model)
        {
            if (!ModelState.IsValid)
            {
                Logger.Trace("Reset password failed: invalid model state");
                return View(model);
            }
            var user = await UserManager.FindByNameAsync(model.Email);
            if (user == null)
            {
                Logger.Trace("Reset password failed: user not found");
                // Don't reveal that the user does not exist
                return RedirectToAction("ResetPasswordConfirmation", "Account");
            }
            var result = await UserManager.ResetPasswordAsync(user.Id, model.Code, model.Password);
            if (result.Succeeded)
            {
                AddUserActvity(user, "PasswordReset", "");
                return RedirectToAction("ResetPasswordConfirmation", "Account");
            }
            Logger.Trace("Reset password failed: {0}", string.Join("; ", result.Errors));
            AddErrors(result);
            return View();
        }

        //
        // GET: /Account/ResetPasswordConfirmation
        [AllowAnonymous]
        public ActionResult ResetPasswordConfirmation()
        {
            return View();
        }

        //
        // POST: /Account/ExternalLogin
        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public ActionResult ExternalLogin(string provider, string returnUrl)
        {
            var optimizedReturnUrl = ProcessQueryParams(returnUrl, (queryString) =>
            {
                queryString.Remove("appid");
                queryString.Remove("userid");
                queryString.Remove("wa");
                queryString.Remove("wreply");
            });

            var routeValues = new Dictionary<string, object>
            {
                {"wreply", RequestHelper.GetParameterFromReturnUrl("wreply")},
                {"ReturnUrl", optimizedReturnUrl}, // For the MicrosoftAccountAuthentication Provider the ReturnUrl is too long, so use an optimized version
            };
            Consts.QueryStringHmacParameterNames.ForEach(x => routeValues.Add(x, HttpContext.Request.QueryString[x]));
            return _federationService.PerformExternalLogin(ControllerContext.HttpContext, provider, Url.Action("ExternalLoginCallback", "Account", new RouteValueDictionary(routeValues)));
        }

        //
        // GET: /Account/SendCode
        [AllowAnonymous]
        public async Task<ActionResult> SendCode(string returnUrl, bool rememberMe)
        {
            var userId = await SignInManager.GetVerifiedUserIdAsync();
            if (userId == null)
            {
                Logger.Trace("Send code failed: user not found");
                return View("Error");
            }
            var userFactors = await UserManager.GetValidTwoFactorProvidersAsync(userId);
            var factorOptions = userFactors.Select(purpose => new SelectListItem { Text = purpose, Value = purpose }).ToList();
            return View(new SendCodeViewModel { Providers = factorOptions, ReturnUrl = returnUrl, RememberMe = rememberMe });
        }

        //
        // POST: /Account/SendCode
        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> SendCode(SendCodeViewModel model)
        {
            if (!ModelState.IsValid)
            {
                Logger.Trace("Send code failed: model state invalid");
                return View();
            }

            // Generate the token and send it
            if (!await SignInManager.SendTwoFactorCodeAsync(model.SelectedProvider))
            {
                Logger.Trace("Send code failed: SendTwoFactorCodeAsync failed");
                return View("Error");
            }
            return RedirectToAction("VerifyCode", new { Provider = model.SelectedProvider, ReturnUrl = model.ReturnUrl, RememberMe = model.RememberMe });
        }

        [ExportModelStateToTempData]
        public async Task<ActionResult> ExternalLoginCallbackAddLogin(string returnUrl)
        {
            var loginInfo = await AuthenticationManager.GetExternalLoginInfoAsync();
            if (loginInfo == null)
            {
                Logger.Trace("External login failed: external login info is null");
                ModelState.AddModelError("", Resources.Global.UnableToProcessLoginMessage);
                return RedirectToAction("Login");
            }
            var user = await _userStore.FindByEmailAsync(loginInfo.Email);
            if (!User.Identity.IsAuthenticated)
            {
                Logger.Trace("External add login failed: User is not logged in.");
                ModelState.AddModelError("", Resources.Global.LoginAndTryAgainMessage);
                return RedirectToAction("Login");
            }
            await AddLoginToUser(user, loginInfo);
            return Redirect(returnUrl);
        }

        //
        // GET: /Account/ExternalLoginCallback
        [AllowAnonymous, ExportModelStateToTempData]
        public async Task<ActionResult> ExternalLoginCallback(string returnUrl)
        {
            returnUrl = ProcessQueryParams(returnUrl, (queryString) =>
            {
                queryString.Add("wreply", Request.QueryString["wreply"]);
                queryString.Add("wa", SignIn);
                queryString.Add("appid", Request.QueryString["appid"]);
                queryString.Add("userid", Request.QueryString["userid"]);
            });
            if (returnUrl == null)
            {
                Logger.Trace("External login failed: returnUrl is null");
            }
            var loginInfo = await AuthenticationManager.GetExternalLoginInfoAsync();
            if (loginInfo == null)
            {
                Logger.Trace("External login failed: external login info is null");
            }
            if (returnUrl == null || loginInfo == null) {
                ModelState.AddModelError("", Resources.Global.UnableToProcessLoginMessage);
                return RedirectToAction("Login");
            }

            // Sign in the user with this external login provider if the user already has a login
            SignInStatus result;
            try
            {
                result = await SignInManager.ExternalSignInAsync(loginInfo, isPersistent: false);
            }
            catch (NotRegisteredForServiceException)
            {
                HandleUserNotRegisteredForService(loginInfo.Email);
                ViewBag.RouteValues["ReturnUrl"] = returnUrl;
                return RedirectToAction("Login", ViewBag.RouteValues);
            }
            catch (AppNotAllowedException)
            {
                return await SelectAppOrRedirect(loginInfo.Email);
            }

            switch (result)
            {
                case SignInStatus.Success:
                    return RedirectToLocal(returnUrl);
                case SignInStatus.LockedOut:
                    Logger.Trace("External login failed: user {0} is locked out", loginInfo.Email);
                    return View("Lockout");
                case SignInStatus.RequiresVerification:
                    Logger.Trace("External login failed: user {0} requires verification", loginInfo.Email);
                    return RedirectToAction("SendCode", new { ReturnUrl = returnUrl, RememberMe = false });
                case SignInStatus.Failure:
                default:
                    // Microsoft account: Try to find an email address if not already contained in the loginInfo
                    if (string.IsNullOrWhiteSpace(loginInfo.Email) && loginInfo.Login.LoginProvider == "Microsoft")
                    {
                        var externalIdentity = await AuthenticationManager.GetExternalIdentityAsync(DefaultAuthenticationTypes.ExternalCookie);
                        var emailClaim = externalIdentity.Claims.FirstOrDefault(x => x.Type.Equals(
                                                                            "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress",
                                                                            StringComparison.OrdinalIgnoreCase));
                        loginInfo.Email = emailClaim?.Value;
                    }
                    // Show external login confirmation if no email is present
                    if (string.IsNullOrWhiteSpace(loginInfo.Email))
                    {
                        Logger.Trace("External login: user {0} ({1}) does not have an account and no email is provided", loginInfo.Login.ProviderKey, loginInfo.Login.LoginProvider);
                        return RedirectToExternalLoginConfirmation(returnUrl, loginInfo, false);
                    }

                    var user = await _userStore.FindByEmailAsync(loginInfo.Email);
                    // For new users silently create an account and return
                    if (user == null)
                    {
                        Logger.Trace("External login: user {0} does not have an account", loginInfo.Email);
                        user = await CreateExternalUser(loginInfo.Email, loginInfo);
                        if (user == null)
                        {
                            ModelState.AddModelError("", Resources.Global.InternalErrorMessage);
                            Logger.Log(LogLevel.Error, "Unable to create the user.");
                            return RedirectToAction("Login", ViewBag.RouteValues);
                        }
                        await SignInManager.SignInAsync(user, isPersistent: false, rememberBrowser: false);
                        return RedirectToLocal(returnUrl);
                    }

                    // For existing users without password show external login info, as they most likely are connected with another external login provider already
                    if (string.IsNullOrWhiteSpace(user.PasswordHash))
                    {
                        Logger.Trace("External login: user {0} ({1}) seems to be connected with another external login provider already ({2})", loginInfo.Login.ProviderKey, loginInfo.Login.LoginProvider, loginInfo.Email);
                        ViewBag.Message = await GenerateLoginSuggestionMessage(user);
                        return View("ExternalLoginInfo");
                    }

                    // For existing users that are not connected yet show login confirmation
                    if (!(await _userStore.GetLoginsAsync(user)).Any(x =>
                        loginInfo.Login.LoginProvider == x.LoginProvider &&
                        loginInfo.Login.ProviderKey == x.ProviderKey))
                    {
                        Logger.Trace("External login: user {0} ({1}) is not yet connected to existing account ({2})", loginInfo.Login.ProviderKey, loginInfo.Login.LoginProvider, loginInfo.Email);
                        return RedirectToExternalLoginConfirmation(returnUrl, loginInfo, true);
                    }
                    // Otherwise show an error
                    Logger.Trace("External login: unable to login user {2} - {0} ({1})", loginInfo.Login.ProviderKey, loginInfo.Login.LoginProvider, loginInfo.Email);
                    return View("ExternalLoginFailure");
            }
        }

        private ActionResult RedirectToExternalLoginConfirmation(string returnUrl, ExternalLoginInfo loginInfo, bool requireLogin)
        {
            ViewBag.RequireLogin = requireLogin;
            ViewBag.ReturnUrl = returnUrl;
            ViewBag.RouteValues["ReturnUrl"] = returnUrl;
            ViewBag.LoginProvider = loginInfo.Login.LoginProvider;
            return View("ExternalLoginConfirmation", new ExternalLoginConfirmationViewModel {Email = loginInfo.Email});
        }

        //
        // POST: /Account/ExternalLoginConfirmation
        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<ActionResult> ExternalLoginConfirmation(ExternalLoginConfirmationViewModel model, string returnUrl)
        {
            if (User.Identity.IsAuthenticated)
            {
                return RedirectToAction("Index", "Manage");
            }

            ViewBag.ReturnUrl = returnUrl;
            ViewBag.RouteValues["ReturnUrl"] = returnUrl;

            if (!ModelState.IsValid)
            {
                Logger.Trace("External login confirmation failed: invalid model state");
                return View(model);
            }

            // Get the information about the user from the external login provider
            var info = await AuthenticationManager.GetExternalLoginInfoAsync();
            if (info == null)
            {
                Logger.Trace("External login confirmation failed: invalid external login info");
                return View("ExternalLoginFailure");
            }
            var user = await _userStore.FindByEmailAsync(model.Email);
            if (user != null)
            {
                // User exists

                // If there is no password set, an external login already should be connected, so just refer to that

                if (string.IsNullOrWhiteSpace(user.PasswordHash))
                {
                    Logger.Trace("External login confirmation: user {0} ({1}) seems to be connected with another external login provider already ({2})", info.Login.ProviderKey, info.Login.LoginProvider, user.Email);
                    ViewBag.Message = await GenerateLoginSuggestionMessage(user);
                    return View("ExternalLoginInfo");
                }

                // Add external login to existing account

                ViewBag.RequireLogin = true;
                ViewBag.LoginProvider = info.Login.LoginProvider;

                // Show Login Form
                if (model.Password == null)
                {
                    Logger.Trace("External login confirmation: account {0} exists, link external login {1}", user.Email, info.Login.LoginProvider);
                    ModelState.AddModelError("", Resources.Global.LoginToLinkWithX.Replace("LoginProvider", info.Login.LoginProvider));
                    return View(model);
                }

                // Process Login
                if (string.IsNullOrEmpty(model.Password))
                {
                    ModelState.AddModelError("password", string.Format(Resources.Global.RequiredAttribute_ValidationError, Resources.Global.Password));
                }
                if (!ModelState.IsValid)
                {
                    Logger.Trace("External login confirmation: model state invalid");
                    return View(model);
                }
                SignInStatus result;
                try
                {
                    result = await SignInManager.PasswordSignInAsync(model.Email, model.Password, false, false);
                }
                catch (NotRegisteredForServiceException)
                {
                    HandleUserNotRegisteredForService(model.Email);
                    return View(model);
                }
                catch (AppNotAllowedException)
                {
                    return await SelectAppOrRedirect(model.Email);
                }
                switch (result)
                {
                    case SignInStatus.Success:
                        await AddLoginToUser(user, info);
                        break;
                    case SignInStatus.LockedOut:
                        return View("Lockout");
                    case SignInStatus.RequiresVerification:
                        ViewBag.RouteValues.ReturnUrl = returnUrl;
                        ViewBag.RouteValues.RememberMe = false;
                        return RedirectToAction("SendCode", ViewBag.RouteValues);
                    case SignInStatus.Failure:
                    default:
                        ModelState.AddModelError("", Resources.Global.InvalidLoginAttempt);
                        Logger.Trace("External login confirmation failed: user {0}, status {1}", model.Email, result);
                        return View(model);
                }
            }
            else
            {
                user = await CreateExternalUser(model.Email, info);
                if (user == null)
                {
                    ModelState.AddModelError("", Resources.Global.InternalErrorMessage);
                    Logger.Log(LogLevel.Error, "Unable to create the user.");
                    return View(model);
                }
                await SignInManager.SignInAsync(user, isPersistent: false, rememberBrowser: false);
            }
            return RedirectToLocal(returnUrl);
        }

        private async Task AddLoginToUser(AppMember user, ExternalLoginInfo info)
        {
            if (!(await _userStore.GetLoginsAsync(user)).Any(x =>
                info.Login.LoginProvider == x.LoginProvider &&
                info.Login.ProviderKey == x.ProviderKey))
            {
                AddAssignAccountActivity(user, info.Login.LoginProvider);
                await _userStore.AddLoginAsync(user, info.Login);
            }
        }

        /// <summary>
        /// Logout user if not registered for current service, adds error.
        /// </summary>
        private void HandleUserNotRegisteredForService(string email)
        {
            Logger.Trace("Login failed: user {0} not registered for service {1}", email, RequestHelper.GetCurrentServiceId());
            ModelState.AddModelError("", Resources.Global.InvalidLoginAttempt + " " + Resources.Global.NoPermissionsForService);
            // On service id mismatch user might be logged in, but should not
            // Using ApplicationCookie because of https://aspnetidentity.codeplex.com/workitem/2347
            SignOut();
        }

        private async Task<AppMember> CreateExternalUser(string email, ExternalLoginInfo info)
        {
            var user = new AppMember {UserName = email, Email = email};
            var result = await UserManager.CreateAsync(user);
            if (result.Succeeded)
            {
                AddRegisterActivity(user, info.Login.LoginProvider);
                result = await UserManager.AddLoginAsync(user.Id, info.Login);
                if (result.Succeeded)
                {
                    return await Task.FromResult(user);
                }
                else
                {
                    Logger.Trace("Create user failed: {0}", string.Join("; ", result.Errors));
                    AddErrors(result);
                }
            }
            else
            {
                Logger.Trace("Create user failed: {0}", string.Join("; ", result.Errors));
                AddErrors(result);
            }
            return await Task.FromResult<AppMember>(null);
        }

        //
        // POST: /Account/LogOff
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult LogOff()
        {
            SignOut();
            var returnUrl = Request.QueryString["returnUrl"];
            return string.IsNullOrEmpty(returnUrl) ? (ActionResult) RedirectToAction("About", "Home") : Redirect(returnUrl);
        }

        //
        // GET: /Account/ExternalLoginFailure
        [AllowAnonymous]
        public ActionResult ExternalLoginFailure()
        {
            return View();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_userManager != null)
                {
                    _userManager.Dispose();
                    _userManager = null;
                }

                if (_signInManager != null)
                {
                    _signInManager.Dispose();
                    _signInManager = null;
                }
            }

            base.Dispose(disposing);
        }

        #region Helpers

        private IAuthenticationManager AuthenticationManager
        {
            get
            {
                return HttpContext.GetOwinContext().Authentication;
            }
        }

        private void AddErrors(IdentityResult result)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError("", error);
            }
        }

        private ActionResult RedirectToLocal(string returnUrl)
        {
            if (Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }
            return RedirectToAction("Index", "Home");
        }

        private static string ProcessQueryParams(string url, Action<NameValueCollection> processor)
        {
            var queryStringStartIndex = url.IndexOf("?", StringComparison.Ordinal);
            var returnUrlPrefix = url.Substring(0, queryStringStartIndex);
            var queryString = HttpUtility.ParseQueryString(url.Substring(queryStringStartIndex + 1));
            processor(queryString);
            return returnUrlPrefix + "?" + queryString;
        }

        private void AddRegisterActivity(AppMember user, string provider)
        {
            AddUserActvity(user, "Register", "Provider: " + provider);
        }

        private void AddAssignAccountActivity(AppMember user, string provider)
        {
            AddUserActvity(user, "AssignAccount", "Provider: " + provider);
        }

        private void AddUserActvity(AppMember user, string type, string details)
        {
            if (string.IsNullOrWhiteSpace(user?.Id))
            {
                return;
            }
            _userActivityRepository.Insert(new UserActivity
            {
                AppId = RequestHelper.GetCurrentAppId() ?? Guid.Empty.ToString(),
                ServiceId = RequestHelper.GetCurrentServiceId() ?? Guid.Empty.ToString(),
                DateTime = DateTime.UtcNow,
                Type = type,
                UserId = user.Id,
                Details = details
            });
        }

        #endregion
    }
}