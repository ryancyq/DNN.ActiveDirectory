'
' DotNetNuke� - http://www.dotnetnuke.com
' Copyright (c) 2002-2013
' by DotNetNuke Corporation
'
' Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated 
' documentation files (the "Software"), to deal in the Software without restriction, including without limitation 
' the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and 
' to permit persons to whom the Software is furnished to do so, subject to the following conditions:
'
' The above copyright notice and this permission notice shall be included in all copies or substantial portions 
' of the Software.
'
' THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED 
' TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL 
' THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF 
' CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER 
' DEALINGS IN THE SOFTWARE.
'
Imports DotNetNuke.Authentication.ActiveDirectory.ADSI
Imports DotNetNuke.Common
Imports DotNetNuke.Entities.Modules
Imports DotNetNuke.Entities.Portals
Imports DotNetNuke.Security.Membership
Imports DotNetNuke.Entities.Users
Imports DotNetNuke.Common.Utilities
Imports DotNetNuke.Services.Log.EventLog
Imports System.Xml.XPath
Imports System.Xml

Imports DNNUserController = DotNetNuke.Entities.Users.UserController

Namespace DotNetNuke.Authentication.ActiveDirectory
    Public Class AuthenticationController
        Inherits UserUserControlBase

        Private ReadOnly _mProviderTypeName As String = ""
        Private ReadOnly _portalSettings As PortalSettings

        ''' -------------------------------------------------------------------
        ''' <summary>
        ''' </summary>
        ''' <remarks>
        ''' </remarks>
        ''' <history>
        '''     [tamttt]	08/01/2004	Created
        ''' </history>
        ''' -------------------------------------------------------------------
        Sub New()
            Dim config As Configuration = Configuration.GetConfig()
            _portalSettings = PortalController.Instance.GetCurrentPortalSettings
            _mProviderTypeName = config.ProviderTypeName
        End Sub

        ''' -------------------------------------------------------------------
        ''' <summary>
        ''' </summary>
        ''' <remarks>
        ''' </remarks>
        ''' <history>
        '''     [tamttt]	08/01/2004	Created
        '''     [mhorton]     12/07/2008  ACD-7488
        ''' </history>
        ''' -------------------------------------------------------------------
        Public Function AuthenticationLogon(ByVal RedirectOnAuthenticationFailed As Boolean) As Boolean
            Dim objAuthUserController As New UserController
            Dim objReturnUser As UserInfo = Nothing
            Dim loggedOnUserName As String = HttpContext.Current.Request.ServerVariables(Configuration.LOGON_USER_VARIABLE)
            Dim loginStatus As UserLoginStatus = UserLoginStatus.LOGIN_FAILURE

            ' Get ipAddress for eventLog
            Dim ipAddress As String = ""
            If Not HttpContext.Current.Request.UserHostAddress Is Nothing Then
                ipAddress = HttpContext.Current.Request.UserHostAddress
            End If

            If (loggedOnUserName.Length > 0) Then
                Dim objUser As UserInfo
                Dim objAuthUser As ADUserInfo
                objAuthUser = objAuthUserController.GetUser(loggedOnUserName)
                objUser = DNNUserController.GetUserByName(_portalSettings.PortalId, loggedOnUserName)

                objReturnUser = AuthenticateUser(objUser, objAuthUser, loginStatus, ipAddress)


                If Not (objReturnUser Is Nothing) Then

                    objAuthUser.LastIPAddress = ipAddress
                    UpdateDNNUser(objReturnUser, objAuthUser)

                    FormsAuthentication.SetAuthCookie(Convert.ToString(loggedOnUserName), True)

                    SetStatus(_portalSettings.PortalId, AuthenticationStatus.WinLogon)

                    'check if user has supplied custom value for expiration
                    Dim persistentCookieTimeout As Integer
                    If Not Config.GetSetting("PersistentCookieTimeout") Is Nothing Then
                        persistentCookieTimeout = Integer.Parse(Config.GetSetting("PersistentCookieTimeout"))
                        'only use if non-zero, otherwise leave as asp.net value
                        If persistentCookieTimeout <> 0 Then
                            'locate and update cookie
                            Dim authCookie As String = FormsAuthentication.FormsCookieName
                            For Each cookie As String In HttpContext.Current.Response.Cookies
                                If cookie.Equals(authCookie) Then
                                    HttpContext.Current.Response.Cookies(cookie).Expires =
                                        DateTime.Now.AddMinutes(persistentCookieTimeout)
                                End If
                            Next
                        End If
                    End If

                    Dim objEventLog As New EventLogController
                    Dim objEventLogInfo As New LogInfo
                    objEventLogInfo.AddProperty("IP", ipAddress)
                    objEventLogInfo.LogPortalID = _portalSettings.PortalId
                    objEventLogInfo.LogPortalName = _portalSettings.PortalName
                    objEventLogInfo.LogUserID = objReturnUser.UserID
                    objEventLogInfo.LogUserName = loggedOnUserName
                    objEventLogInfo.AddProperty("WindowsAuthentication", "True")
                    objEventLogInfo.LogTypeKey = "LOGIN_SUCCESS"

                    objEventLog.AddLog(objEventLogInfo)
                Else

                End If
            Else
                ' Not Windows Authentication
            End If

            If ((objReturnUser IsNot Nothing) OrElse (RedirectOnAuthenticationFailed)) Then
                'Updated to redirect to querrystring passed in prior to authentication
                Dim querystringparams As String = "logon=" & DateTime.Now.Ticks.ToString()
                Dim strUrl As String = NavigateURL(_portalSettings.ActiveTab.TabID, String.Empty, querystringparams)

                If Not HttpContext.Current.Request.Cookies("DNNReturnTo") Is Nothing _
                    Then
                    querystringparams =
                        HttpContext.Current.Request.Cookies("DNNReturnTo").Value
                    'ACD-8445
                    If querystringparams <> String.Empty Then querystringparams = querystringparams.ToLower
                    If querystringparams <> String.Empty And querystringparams.IndexOf("windowssignin.aspx") < 0 Then _
                        strUrl = querystringparams
                End If
                HttpContext.Current.Response.Redirect(strUrl, True)
            End If

            Return (objReturnUser IsNot Nothing)

        End Function

        ''' -------------------------------------------------------------------
        ''' <summary>
        ''' </summary>
        ''' <remarks>
        ''' </remarks>
        ''' <history>
        '''     [mhorton]	05/23/2009	Created
        '''     [mhorton]	03/22/2011	Fixed Item 6365
        ''' </history>
        ''' -------------------------------------------------------------------
        Public Function ManualLogon(ByVal userName As String, ByVal strPassword As String,
                                     ByRef loginStatus As UserLoginStatus, ByVal ipAddress As String) As UserInfo
            Dim objAuthUser As ADUserInfo = ProcessFormAuthentication(userName, strPassword)
            Dim _config As Configuration = Configuration.GetConfig()
            Dim objUser As UserInfo = Nothing
            Dim objReturnUser As UserInfo = Nothing

            If (userName.Length > 0) And (objAuthUser IsNot Nothing) Then
                If _config.StripDomainName Then
                    userName = Utilities.TrimUserDomainName(userName)
                End If
                objAuthUser.Username = userName
                objUser = DNNUserController.GetUserByName(_portalSettings.PortalId, userName)

                objReturnUser = AuthenticateUser(objUser, objAuthUser, loginStatus, ipAddress)
                If Not (objReturnUser Is Nothing) Then
                    objAuthUser.LastIPAddress = ipAddress
                    UpdateDNNUser(objReturnUser, objAuthUser)
                End If
            End If

            Return objReturnUser

        End Function

        ''' -------------------------------------------------------------------
        ''' <summary>
        '''    Process the authentication of the user whether they've logged in 
        '''    manually or automatically
        ''' </summary>
        ''' <remarks>
        ''' </remarks>
        ''' <history>
        '''     [mhorton]	02/19/2012	Created
        '''     [sawest]    12/16/2016  Added enable password retrieval check before getting aspnetuser password
        ''' </history>
        ''' -------------------------------------------------------------------
        Public Function AuthenticateUser(ByVal objUser As UserInfo, ByVal objAuthUser As ADUserInfo,
                                     ByRef loginStatus As UserLoginStatus, ByVal ipAddress As String) As UserInfo
            Dim _config As Configuration = Configuration.GetConfig()
            Dim objReturnUser As UserInfo = Nothing

            If Not (objUser Is Nothing) Then
                Dim aspNetUser As MembershipUser = Web.Security.Membership.GetUser(objUser.Username)
                Dim strPassword As String

                If Web.Security.Membership.Provider.EnablePasswordRetrieval And Web.Security.Membership.Provider.PasswordFormat <> MembershipPasswordFormat.Hashed Then
                    strPassword = RandomizePassword(aspNetUser, objUser, aspNetUser.GetPassword())
                Else
                    strPassword = RandomizePassword(aspNetUser, objUser, "")
                End If

                If (objUser.IsDeleted = False) Then

                    objReturnUser =
                            DNNUserController.ValidateUser(_portalSettings.PortalId, objUser.Username, strPassword,
                                                            "Active Directory", _portalSettings.PortalName, ipAddress,
                                                            loginStatus)
                    ' Synchronize role membership if it's required in settings
                    If _config.SynchronizeRole Then
                        SynchronizeRoles(objReturnUser)
                    End If
                Else
                    'Only create user if Allowed to
                    'ACD-4259
                    'Item 7703
                    If Not _config.AutoCreateUsers = True Then
                        objUser.IsDeleted = False
                        objUser.Membership.IsDeleted = False
                        objUser.Membership.Password = strPassword
                        DNNUserController.UpdateUser(_portalSettings.PortalId, objUser)
                        CreateUser(objUser, loginStatus)
                        If loginStatus = UserLoginStatus.LOGIN_SUCCESS Then
                            objReturnUser =
                                    DNNUserController.GetUserByName(_portalSettings.PortalId, objAuthUser.Username)
                            If _config.SynchronizeRole Then
                                SynchronizeRoles(objReturnUser)
                            End If
                        End If
                    End If
                End If
            Else
                'Only create user if Allowed to
                'ACD-4259
                If Not _config.AutoCreateUsers = True Then
                    'User doesn't exist in this portal. Make sure user doesn't exist on any other portal
                    objUser = DNNUserController.GetUserByName(Null.NullInteger, objAuthUser.Username)
                    objAuthUser.Membership.Password = Utilities.GetRandomPassword()
                    If objUser Is Nothing Then 'User doesn't exist in any portal
                        'Item 6365
                        'objAuthUser.Membership.Password = Utilities.GetRandomPassword() 'moved up Steven A West 1/11/2018
                        Dim objDnnUserInfo As New UserInfo
                        objDnnUserInfo.AffiliateID = objAuthUser.AffiliateID
                        objDnnUserInfo.DisplayName = objAuthUser.DisplayName
                        objDnnUserInfo.Email = objAuthUser.Email
                        objDnnUserInfo.FirstName = objAuthUser.FirstName
                        objDnnUserInfo.IsDeleted = objAuthUser.IsDeleted
                        objDnnUserInfo.IsSuperUser = objAuthUser.IsSuperUser
                        objDnnUserInfo.LastIPAddress = ipAddress
                        objDnnUserInfo.LastName = objAuthUser.LastName
                        objDnnUserInfo.Membership = objAuthUser.Membership
                        objDnnUserInfo.PortalID = objAuthUser.PortalID
                        objDnnUserInfo.Profile = objAuthUser.Profile
                        'Deprecated in DNN 6.2
                        'objDnnUserInfo.RefreshRoles = objAuthUser.RefreshRoles
                        objDnnUserInfo.Roles = objAuthUser.Roles
                        objDnnUserInfo.Username = objAuthUser.Username
                        CreateUser(objDnnUserInfo, loginStatus)
                    Else 'user exists in another portal
                        'No need to use randomizepassword here useing getrandompassword instead 
                        'password set above - Steven A West 1/11/2018 regarding #23
                        'objAuthUser.Membership.Password = RandomizePassword(objUser, "") 
                        objAuthUser.UserID = objUser.UserID
                        CreateUser(CType(objAuthUser, UserInfo), loginStatus)
                    End If
                    If loginStatus = UserLoginStatus.LOGIN_SUCCESS Then
                        objReturnUser =
                            DNNUserController.GetUserByName(_portalSettings.PortalId, objAuthUser.Username)
                        'intUserId = objReturnUser.UserID
                        If _config.SynchronizeRole Then
                            SynchronizeRoles(objReturnUser)
                        End If
                    End If
                End If
            End If
            Return objReturnUser
        End Function

        ''' -------------------------------------------------------------------
        ''' <summary>
        '''      Updates the DNN profile with information pulled from the Active Directory
        ''' </summary>
        ''' <remarks>
        ''' </remarks>
        ''' <history>
        '''     [mhorton]	02/19/2012	Created
        '''     [mhorton]	02/19/2012	Fixed Item 7739 Only updates the profile if information is pulled from the Active Directory.
        '''     [sawest]    12/16/2016  Added photo item
        ''' </history>
        ''' -------------------------------------------------------------------
        Private Sub UpdateDNNUser(ByVal objReturnUser As UserInfo, ByVal objAuthUser As ADUserInfo)

            With objReturnUser
                If Not (objAuthUser.DisplayName = "") Then
                    .DisplayName = objAuthUser.DisplayName
                End If
                If Not (objAuthUser.Email = "") Then
                    .Email = objAuthUser.Email
                End If
                If Not (objAuthUser.FirstName = "") Then
                    .FirstName = objAuthUser.FirstName
                End If
                If Not (objAuthUser.LastIPAddress = "") Then
                    .LastIPAddress = objAuthUser.LastIPAddress
                End If
                If Not (objAuthUser.LastName = "") Then
                    .LastName = objAuthUser.LastName
                End If
                If Not (objAuthUser.Profile.FirstName = "") Then
                    .Profile.FirstName = objAuthUser.Profile.FirstName
                End If
                If Not (objAuthUser.Profile.LastName Is Nothing) Then
                    .Profile.LastName = objAuthUser.Profile.LastName
                End If
                If Not (objAuthUser.Profile.Street = "") Then
                    .Profile.Street = objAuthUser.Profile.Street
                End If
                If Not (objAuthUser.Profile.City = "") Then
                    .Profile.City = objAuthUser.Profile.City
                End If
                If Not (objAuthUser.Profile.Region = "") Then
                    .Profile.Region = objAuthUser.Profile.Region
                End If
                If Not (objAuthUser.Profile.PostalCode = "") Then
                    .Profile.PostalCode = objAuthUser.Profile.PostalCode
                End If
                If Not (objAuthUser.Profile.Country = "") Then
                    .Profile.Country = objAuthUser.Profile.Country
                End If
                If Not (objAuthUser.Profile.Telephone = "") Then
                    .Profile.Telephone = objAuthUser.Profile.Telephone
                End If
                If Not (objAuthUser.Profile.Fax = "") Then
                    .Profile.Fax = objAuthUser.Profile.Fax
                End If
                If Not (objAuthUser.Profile.Cell = "") Then
                    .Profile.Cell = objAuthUser.Profile.Cell
                End If
                If Not (objAuthUser.Profile.Fax = "") Then
                    .Profile.Fax = objAuthUser.Profile.Fax
                End If
                If Not (objAuthUser.Profile.Website = "") Then
                    .Profile.Website = objAuthUser.Profile.Website
                End If
                If Not (objAuthUser.Profile.Photo = "") Then
                    .Profile.Photo = objAuthUser.Profile.Photo
                End If
            End With
            Dim objAuthUserController As New UserController
            objAuthUserController.UpdateDnnUser(objReturnUser)
        End Sub

        ''' -------------------------------------------------------------------
        ''' <summary>
        ''' </summary>
        ''' <remarks>
        ''' </remarks>
        ''' <history>
        '''     [mhorton]	22/05/2008	Created
        ''' </history>
        ''' -------------------------------------------------------------------
        Private Sub CreateUser(ByVal objUser As UserInfo, ByRef loginStatus As UserLoginStatus)
            UpdateDisplayName(objUser)
            objUser.Membership.Approved = True

            Dim createStatus As UserCreateStatus = DNNUserController.CreateUser(objUser)

            Dim args As UserCreatedEventArgs
            If createStatus = UserCreateStatus.Success Then
                args = New UserCreatedEventArgs(objUser)
            Else ' registration error
                args = New UserCreatedEventArgs(Nothing)
            End If
            args.CreateStatus = createStatus
            OnUserCreated(args)
            OnUserCreateCompleted(args)

            'Item 7703
            If createStatus = UserCreateStatus.Success Or createStatus = UserCreateStatus.UserAlreadyRegistered Then
                loginStatus = UserLoginStatus.LOGIN_SUCCESS
            Else
                loginStatus = UserLoginStatus.LOGIN_FAILURE
            End If
        End Sub

        ''' -----------------------------------------------------------------------------
        ''' <summary>
        ''' RandomizePassword = Creates a random password to be stored in the database
        ''' </summary>
        ''' <param name="objUser">DNN User Object</param>
        ''' <history>
        '''     [mhorton]   12/10/2008 - ACD-4158
        '''     [sawest]    12/16/2016  Added check for enable password retrieval
        '''     [sawest]    12/16/2016  Switched changepassword to changepasswordbytoken to support encrypted passwords
        ''' </history>
        ''' -----------------------------------------------------------------------------
        Private Function RandomizePassword(ByVal objUser As UserInfo, ByRef strPassword As String) As String
            'ACD-4158 - Make sure password in the DNN database does not match that of the password in the AD.

            Dim aspNetUser As MembershipUser = Web.Security.Membership.GetUser(objUser.Username)
            Dim strStoredPassword As String = ""
            If Web.Security.Membership.Provider.EnablePasswordRetrieval And Web.Security.Membership.Provider.PasswordFormat <> MembershipPasswordFormat.Hashed Then
                strStoredPassword = aspNetUser.GetPassword()
            End If

            If strStoredPassword = strPassword Or String.IsNullOrEmpty(strStoredPassword) Then
                Dim strRandomPassword As String = Utilities.GetRandomPassword()
                DNNUserController.ResetPasswordToken(objUser, 2)
                DNNUserController.ChangePasswordByToken(PortalSettings.PortalId, objUser.Username, strRandomPassword, objUser.PasswordResetToken.ToString)
                Return strRandomPassword
            Else
                Return strStoredPassword
            End If
        End Function
        ''' <summary>
        ''' RandomizePassword = Creates a random password to be stored in the database
        ''' First function calls the GetUser function again.  Need an overloaded function that can accept a MembershipUser so GetUser is not called twice.
        ''' </summary>
        ''' <param name="aspNetUser">MembershipUser object</param>
        ''' <param name="objUser">DNN User Object</param>
        ''' <param name="strPassword">Password</param>
        ''' <returns></returns>
        ''' <history>
        '''     [sawest]    12/16/2016  Created function
        ''' </history>
        Private Function RandomizePassword(ByVal aspNetUser As MembershipUser, ByVal objUser As UserInfo, ByRef strPassword As String) As String

            Dim strStoredPassword As String = ""
            If Web.Security.Membership.Provider.EnablePasswordRetrieval And Web.Security.Membership.Provider.PasswordFormat <> MembershipPasswordFormat.Hashed Then
                strStoredPassword = aspNetUser.GetPassword()
            End If

            If strStoredPassword = strPassword Or String.IsNullOrEmpty(strStoredPassword) Then
                Dim strRandomPassword As String = Utilities.GetRandomPassword()
                DNNUserController.ResetPasswordToken(objUser, 2)
                DNNUserController.ChangePasswordByToken(PortalSettings.PortalId, objUser.Username, strRandomPassword, objUser.PasswordResetToken.ToString)
                Return strRandomPassword
            Else
                Return strStoredPassword
            End If
        End Function

        ''' -------------------------------------------------------------------
        ''' <summary>
        ''' </summary>
        ''' <remarks>
        ''' </remarks>
        ''' <history>
        '''     [tamttt]	08/01/2004	Created
        ''' </history>
        ''' -------------------------------------------------------------------
        Public Sub AuthenticationLogoff()
            Dim _portalSettings As PortalSettings = PortalController.Instance.GetCurrentPortalSettings

            ' Log User Off from Cookie Authentication System
            FormsAuthentication.SignOut()
            If GetStatus(_portalSettings.PortalId) = AuthenticationStatus.WinLogon Then
                SetStatus(_portalSettings.PortalId, AuthenticationStatus.WinLogoff)
            End If

            ' expire cookies
            HttpContext.Current.Response.Cookies("portalaliasid").Value = Nothing
            HttpContext.Current.Response.Cookies("portalaliasid").Path = "/"
            HttpContext.Current.Response.Cookies("portalaliasid").Expires = DateTime.Now.AddYears(-30)

            HttpContext.Current.Response.Cookies("portalroles").Value = Nothing
            HttpContext.Current.Response.Cookies("portalroles").Path = "/"
            HttpContext.Current.Response.Cookies("portalroles").Expires = DateTime.Now.AddYears(-30)

            ' Redirect browser back to portal 
            If _portalSettings.HomeTabId <> -1 Then
                HttpContext.Current.Response.Redirect(NavigateURL(_portalSettings.HomeTabId), True)
            Else
                'If (_portalSettings.ActiveTab.IsAdminTab) Then
                '    HttpContext.Current.Response.Redirect("~/" & glbDefaultPage, True)
                'Else
                HttpContext.Current.Response.Redirect(NavigateURL(), True)
                'End If
            End If
        End Sub

        ''' -------------------------------------------------------------------
        ''' <summary>
        ''' </summary>
        ''' <remarks>
        ''' </remarks>
        ''' <history>
        '''     [tamttt]	08/01/2004	Created
        ''' </history>
        ''' -------------------------------------------------------------------
        Public Function ProcessFormAuthentication(ByVal loggedOnUserName As String, ByVal loggedOnPassword As String) _
            As ADUserInfo
            Dim config As Configuration = Configuration.GetConfig()
            Dim objAuthUserController As New UserController

            If config.WindowsAuthentication Then
                Dim userName As String = loggedOnUserName

                If config.StripDomainName Then
                    userName = Utilities.TrimUserDomainName(userName)
                End If

                Dim objAuthUser As ADUserInfo = objAuthUserController.GetUser(userName, loggedOnPassword)
                Return objAuthUser
            End If
            Return Nothing

        End Function

        ''' -------------------------------------------------------------------
        ''' <summary>
        ''' </summary>
        ''' <remarks>
        ''' </remarks>
        ''' <history>
        '''     [tamttt]	08/01/2004	Created
        ''' </history>
        ''' -------------------------------------------------------------------
        Public Function GetDnnUser(ByVal portalId As Integer, ByVal loggedOnUserName As String) As UserInfo
            Dim config As Configuration = Configuration.GetConfig()
            Dim objUser As UserInfo

            Dim userName As String = loggedOnUserName

            If config.StripDomainName Then
                userName = Utilities.TrimUserDomainName(userName)
            End If

            'TODO: Check the new concept of 3.0 for user in multi portal
            ' check if this user exists in database for any portal
            objUser = DNNUserController.GetUserByName(Null.NullInteger, userName)
            If Not objUser Is Nothing Then
                ' Check if user exists in this portal
                If DNNUserController.GetUserByName(portalId, userName) Is Nothing Then
                    ' The user does not exist in this portal - add them
                    objUser.PortalID = portalId
                    DNNUserController.CreateUser(objUser)
                End If
                Return objUser
            Else
                ' the user does not exist
                Return Nothing
            End If

        End Function

        ''' -------------------------------------------------------------------
        ''' <summary>
        ''' </summary>
        ''' <remarks>
        ''' </remarks>
        ''' <history>
        '''     [tamttt]	08/01/2004	Created
        ''' </history>
        ''' -------------------------------------------------------------------
        Public Function AuthenticationTypes() As Array
            Return AuthenticationProvider.Instance(_mProviderTypeName).GetAuthenticationTypes
        End Function

        ''' -------------------------------------------------------------------
        ''' <summary>
        ''' </summary>
        ''' <remarks>
        ''' </remarks>
        ''' <history>
        '''     [tamttt]	08/01/2004	Created
        ''' </history>
        ''' -------------------------------------------------------------------
        Public Function NetworkStatus() As String
            Return AuthenticationProvider.Instance(_mProviderTypeName).GetNetworkStatus()
        End Function

        ''' -------------------------------------------------------------------
        ''' <summary>
        ''' </summary>
        ''' <remarks>
        ''' </remarks>
        ''' <history>
        '''     [tamttt]	08/01/2004	Created
        ''' </history>
        ''' -------------------------------------------------------------------
        Public Shared Function GetStatus(ByVal portalId As Integer) As AuthenticationStatus
            'Dim _portalSettings As PortalSettings = PortalController.GetCurrentPortalSettings
            Dim authCookies As String = Configuration.AUTHENTICATION_STATUS_KEY & "." & portalId.ToString
            Try
                If Not HttpContext.Current.Request.Cookies(authCookies) Is Nothing Then
                    ' get Authentication from cookie
                    Dim _
                        authenticationTicket As FormsAuthenticationTicket =
                            FormsAuthentication.Decrypt(HttpContext.Current.Request.Cookies(authCookies).Value)
                    Return _
                        CType([Enum].Parse(GetType(AuthenticationStatus), authenticationTicket.UserData),
                            AuthenticationStatus)
                Else
                    Return AuthenticationStatus.Undefined
                End If
            Catch ex As Exception
            End Try
        End Function

        ''' -------------------------------------------------------------------
        ''' <summary>
        ''' 
        ''' </summary>
        ''' <remarks>
        ''' </remarks>
        ''' <history>
        '''     [tamttt]	08/01/2004	Created
        '''     [mhorton]	02/10/2012	Get the forms cookie timeout from the web.config - WorkItem:7620
        ''' </history>
        ''' -------------------------------------------------------------------
        Public Shared Sub SetStatus(ByVal portalId As Integer, ByVal status As AuthenticationStatus)
            Dim authCookies As String = Configuration.AUTHENTICATION_STATUS_KEY & "." & portalId.ToString
            Dim request As HttpRequest = HttpContext.Current.Request
            Dim response As HttpResponse = HttpContext.Current.Response
            Dim nTimeOut As Integer = GetAuthCookieTimeout()

            If nTimeOut = 0 Then
                nTimeOut = 60
            End If
            Dim _
                authenticationTicket As _
                    New FormsAuthenticationTicket(1, authCookies, DateTime.Now, DateTime.Now.AddMinutes(nTimeOut), False,
                                                   status.ToString)
            ' encrypt the ticket
            Dim strAuthentication As String = FormsAuthentication.Encrypt(authenticationTicket)

            If Not request.Cookies(authCookies) Is Nothing Then
                ' expire
                request.Cookies(authCookies).Value = Nothing
                request.Cookies(authCookies).Path = "/"
                request.Cookies(authCookies).Expires = DateTime.Now.AddYears(-1)
            End If

            response.Cookies(authCookies).Value = strAuthentication
            response.Cookies(authCookies).Path = "/"
            response.Cookies(authCookies).Expires = DateTime.Now.AddMinutes(nTimeOut)

        End Sub

        ''' -------------------------------------------------------------------
        ''' <summary>
        ''' 
        ''' </summary>
        ''' <remarks>
        '''		[mhorton] Created to prevent duplicate code on role synchronization.
        ''' </remarks>
        ''' <history>
        '''     [mhorton]	12/18/2006	Created
        '''     [mhorton]	10/05/2009	Changed to use IsNotSimplyUser instead of GUID - WorkItem:2943
        '''     [mhorton]   29/05/2011  Fixed code for Item 6735
        ''' </history>
        ''' -------------------------------------------------------------------
        <Obsolete("procedure obsoleted in 5.0.3 - user SynchronizeRoles(ByVal objUser As UserInfo) instead")>
        Public Sub SynchronizeRoles(ByVal loggedOnUserName As String, ByVal intUserId As Integer)
            Dim objAuthUserController As New UserController
            Dim objAuthUser As ADUserInfo

            objAuthUser = objAuthUserController.GetUser(loggedOnUserName)

            ' user object might be in simple version in none active directory network
            If objAuthUser.IsNotSimplyUser Then
                objAuthUser.UserID = intUserId
                UserController.AddUserRoles(_portalSettings.PortalId, objAuthUser)
                'User exists updating user profile
                objAuthUserController.UpdateDnnUser(objAuthUser)
            End If
        End Sub

        ''' -------------------------------------------------------------------
        ''' <summary>
        ''' 
        ''' </summary>
        ''' <remarks>
        '''		[mhorton] Created to prevent duplicate code on role synchronization.
        ''' </remarks>
        ''' <history>
        '''     [mhorton]	12/18/2006	Created
        '''     [mhorton]	10/05/2009	Changed to use IsNotSimplyUser instead of GUID - WorkItem:2943
        '''     [mhorton]	02/09/2012	AD User losing host permissions when logging in - WorkItem:7424
        '''     [mhorton]   02/17/2012 User's profile was getting blanked when getting updated - Item 7739
        ''' </history>
        ''' -------------------------------------------------------------------
        Public Sub SynchronizeRoles(ByVal objUser As UserInfo)
            Dim objAuthUserController As New UserController
            Dim objAuthUser As ADUserInfo

            objAuthUser = objAuthUserController.GetUser(objUser.Username)
            objAuthUser.IsSuperUser = objUser.IsSuperUser
            ' user object might be in simple version in none active directory network
            If objAuthUser.IsNotSimplyUser Then
                objAuthUser.UserID = objUser.UserID
                UserController.AddUserRoles(_portalSettings.PortalId, objAuthUser)
                ''User exists updating user profile
                'objAuthUserController.UpdateDNNUser(objUser)
            End If
        End Sub

        ''' -------------------------------------------------------------------
        ''' <summary>
        ''' This functions updates the display name so that it conforms to 
        ''' Portal rules
        ''' </summary>
        ''' <remarks>
        ''' </remarks>
        ''' <history>
        '''     [mhorton]	02/05/2008	Created 
        ''' </history>
        ''' -------------------------------------------------------------------
        Private Sub UpdateDisplayName(ByVal objDnnUser As UserInfo)
            'Update DisplayName to conform to Format
            Dim _portalSettings As PortalSettings = PortalController.Instance.GetCurrentPortalSettings
            Dim setting As Object = GetSetting(_portalSettings.PortalId, "Security_DisplayNameFormat")
            If (Not setting Is Nothing) AndAlso (Not String.IsNullOrEmpty(Convert.ToString(setting))) Then
                objDnnUser.UpdateDisplayName(Convert.ToString(setting))
            End If
        End Sub

        ''' -------------------------------------------------------------------
        ''' <summary>
        ''' This function pulls the cookie timeout from the web.config.
        ''' </summary>
        ''' <remarks>
        '''		[mhorton] Use only until core version 6.1.0 is the minimum supported version 
        ''' and then call GetAuthCookieTimeout from the core code.
        ''' </remarks>
        ''' <history>
        '''     [mhorton]	02/10/2012	Created in response to WorkItem:7620
        ''' </history>
        ''' -------------------------------------------------------------------
        Public Shared Function GetAuthCookieTimeout() As Integer

            'First check that the script module is installed
            Dim configDoc As XmlDocument = Config.Load()
            Dim formsNav As XPathNavigator = configDoc.CreateNavigator.SelectSingleNode("configuration/system.web/authentication/forms")

            If formsNav Is Nothing Then
                ' Check the new XPath for a wrapped system.web
                formsNav = configDoc.CreateNavigator.SelectSingleNode("configuration/location/system.web/authentication/forms")
            End If
            Return If((formsNav IsNot Nothing), XmlUtils.GetAttributeValueAsInteger(formsNav, "timeout", 30), 0)

        End Function

    End Class
End Namespace
