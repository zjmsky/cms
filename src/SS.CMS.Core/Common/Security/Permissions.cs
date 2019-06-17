﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using SS.CMS.Abstractions;
using SS.CMS.Abstractions.Enums;
using SS.CMS.Abstractions.Models;
using SS.CMS.Abstractions.Repositories;
using SS.CMS.Abstractions.Services;
using SS.CMS.Core.Cache;
using SS.CMS.Core.Cache.Core;
using SS.CMS.Utils;
using SS.CMS.Utils.Enumerations;

namespace SS.CMS.Core.Security
{
    public class Permissions : IPermissions
    {
        private readonly AdministratorInfo _adminInfo;
        private readonly ISettingsManager _settingsManager;
        private readonly IPluginManager _pluginManager;
        private readonly ISiteRepository _siteRepository;
        private readonly IAdministratorsInRolesRepository _administratorsInRolesRepository;
        private readonly IPermissionsInRolesRepository _permissionsInRolesRepository;
        private readonly ISitePermissionsRepository _sitePermissionsRepository;

        private readonly string _rolesKey;
        private readonly string _permissionListKey;
        private readonly string _websitePermissionDictKey;
        private readonly string _channelPermissionDictKey;
        private readonly string _channelPermissionListIgnoreChannelIdKey;
        private readonly string _channelIdListKey;

        private IList<string> _roles;
        private List<string> _permissionList;
        private Dictionary<int, List<string>> _websitePermissionDict;
        private Dictionary<string, List<string>> _channelPermissionDict;
        private List<string> _channelPermissionListIgnoreChannelId;
        private List<int> _channelIdList;

        public Permissions(AdministratorInfo adminInfo, ISettingsManager settingsManager, IPathManager pathManager, IPluginManager pluginManager, ISiteRepository siteRepository, IAdministratorsInRolesRepository administratorsInRolesRepository, IPermissionsInRolesRepository permissionsInRolesRepository, ISitePermissionsRepository sitePermissionsRepository)
        {
            if (adminInfo == null || adminInfo.Locked) return;

            GeneralPermissions = new List<Permission>();
            WebsitePermissions = new List<Permission>();
            WebsiteSysPermissions = new List<Permission>();
            WebsitePluginPermissions = new List<Permission>();
            ChannelPermissions = new List<Permission>();

            _adminInfo = adminInfo;
            _settingsManager = settingsManager;
            _pluginManager = pluginManager;
            _siteRepository = siteRepository;
            _administratorsInRolesRepository = administratorsInRolesRepository;
            _permissionsInRolesRepository = permissionsInRolesRepository;
            _sitePermissionsRepository = sitePermissionsRepository;

            _rolesKey = GetRolesCacheKey(adminInfo.UserName);
            _permissionListKey = GetPermissionListCacheKey(adminInfo.UserName);
            _websitePermissionDictKey = GetWebsitePermissionDictCacheKey(adminInfo.UserName);
            _channelPermissionDictKey = GetChannelPermissionDictCacheKey(adminInfo.UserName);
            _channelPermissionListIgnoreChannelIdKey = GetChannelPermissionListIgnoreChannelIdCacheKey(adminInfo.UserName);
            _channelIdListKey = GetChannelIdListCacheKey(adminInfo.UserName);

            GeneralPermissions.AddRange(_settingsManager.Permissions.App);
            WebsitePermissions.AddRange(_settingsManager.Permissions.Site);
            ChannelPermissions.AddRange(_settingsManager.Permissions.Channel);

            GeneralPermissions.AddRange(_pluginManager.GetTopPermissions());
            var pluginPermissions = _pluginManager.GetSitePermissions(0);
            WebsitePluginPermissions.AddRange(pluginPermissions);
            WebsitePermissions.AddRange(pluginPermissions);
        }

        public List<int> ChannelIdList
        {
            get
            {
                if (_channelIdList != null) return _channelIdList;
                if (_adminInfo == null || _adminInfo.Locked) return new List<int>();

                _channelIdList = DataCacheManager.Get<List<int>>(_channelIdListKey);

                if (_channelIdList == null)
                {
                    _channelIdList = new List<int>();

                    if (!IsSystemAdministrator)
                    {
                        foreach (var dictKey in ChannelPermissionDict.Keys)
                        {
                            var kvp = ParseChannelPermissionDictKey(dictKey);
                            var channelInfo = ChannelManager.GetChannelInfo(kvp.Key, kvp.Value);
                            _channelIdList.AddRange(ChannelManager.GetChannelIdList(channelInfo, ScopeType.All, string.Empty, string.Empty, string.Empty));
                        }
                    }

                    DataCacheManager.InsertMinutes(_channelIdListKey, _channelIdList, 30);
                }
                return _channelIdList ?? (_channelIdList = new List<int>());
            }
        }

        public bool IsSuperAdmin()
        {
            return IsConsoleAdministrator;
        }

        public bool IsSiteAdmin(int siteId)
        {
            return IsSystemAdministrator && GetSiteIdList().Contains(siteId);
        }

        public string GetAdminLevel()
        {
            if (IsConsoleAdministrator)
            {
                return "超级管理员";
            }

            return IsSystemAdministrator ? "站点总管理员" : "普通管理员";
        }

        public List<int> GetSiteIdList()
        {
            var siteIdList = new List<int>();

            if (IsConsoleAdministrator)
            {
                siteIdList = _siteRepository.GetSiteIdList();
            }
            else if (IsSystemAdministrator)
            {
                if (_adminInfo != null)
                {
                    foreach (var siteId in TranslateUtils.StringCollectionToIntList(_adminInfo.SiteIdCollection))
                    {
                        if (!siteIdList.Contains(siteId))
                        {
                            siteIdList.Add(siteId);
                        }
                    }
                }
            }
            else
            {
                var dict = WebsitePermissionDict;

                foreach (var siteId in dict.Keys)
                {
                    if (!siteIdList.Contains(siteId))
                    {
                        siteIdList.Add(siteId);
                    }
                }
            }

            return siteIdList;
        }

        public List<int> GetChannelIdList(int siteId, params string[] permissions)
        {
            if (IsSystemAdministrator)
            {
                return ChannelManager.GetChannelIdList(siteId);
            }

            var siteChannelIdList = new List<int>();
            var dict = ChannelPermissionDict;
            foreach (var dictKey in dict.Keys)
            {
                var kvp = ParseChannelPermissionDictKey(dictKey);
                var dictPermissions = dict[dictKey];
                if (kvp.Key == siteId && dictPermissions.Any(permissions.Contains))
                {
                    var channelInfo = ChannelManager.GetChannelInfo(kvp.Key, kvp.Value);

                    var channelIdList = ChannelManager.GetChannelIdList(channelInfo, ScopeType.All);

                    foreach (var channelId in channelIdList)
                    {
                        if (!siteChannelIdList.Contains(channelId))
                        {
                            siteChannelIdList.Add(channelId);
                        }
                    }
                }
            }

            return siteChannelIdList;
        }

        public bool HasSystemPermissions(params string[] permissions)
        {
            if (IsSystemAdministrator) return true;

            var permissionList = PermissionList;
            return permissions.Any(permission => permissionList.Contains(permission));
        }

        public bool HasSitePermissions(int siteId, params string[] permissions)
        {
            if (IsSystemAdministrator) return true;
            if (!WebsitePermissionDict.ContainsKey(siteId)) return false;

            var websitePermissionList = WebsitePermissionDict[siteId];
            if (websitePermissionList != null && websitePermissionList.Count > 0)
            {
                return permissions.Any(sitePermission => websitePermissionList.Contains(sitePermission));
            }

            return false;
        }

        public bool HasChannelPermissions(int siteId, int channelId, params string[] permissions)
        {
            while (true)
            {
                if (channelId == 0) return false;
                if (IsSystemAdministrator) return true;
                var dictKey = GetChannelPermissionDictKey(siteId, channelId);
                if (ChannelPermissionDict.ContainsKey(dictKey) && HasChannelPermissions(ChannelPermissionDict[dictKey], permissions)) return true;

                var parentChannelId = ChannelManager.GetParentId(siteId, channelId);
                channelId = parentChannelId;
            }
        }

        public bool IsConsoleAdministrator => EPredefinedRoleUtils.IsConsoleAdministrator(Roles);

        public bool IsSystemAdministrator => EPredefinedRoleUtils.IsSystemAdministrator(Roles);

        //public bool IsSuperAdmin(string userName)
        //{
        //    var adminPermissionsImpl = new PermissionsImpl(userName);
        //    return adminPermissionsImpl.IsConsoleAdministrator;
        //}

        //public bool IsSiteAdmin(string userName, int siteId)
        //{
        //    var adminPermissionsImpl = new PermissionsImpl(userName);
        //    return adminPermissionsImpl.IsSystemAdministrator && adminPermissionsImpl.HasSitePermissions(siteId);
        //}

        public List<string> PermissionList
        {
            get
            {
                if (_permissionList != null) return _permissionList;
                if (_adminInfo == null || _adminInfo.Locked) return new List<string>();

                _permissionList = DataCacheManager.Get<List<string>>(_permissionListKey);

                if (_permissionList == null)
                {
                    if (EPredefinedRoleUtils.IsConsoleAdministrator(Roles))
                    {
                        _permissionList = new List<string>();
                        foreach (var permission in GeneralPermissions)
                        {
                            _permissionList.Add(permission.Id);
                        }
                    }
                    else if (EPredefinedRoleUtils.IsSystemAdministrator(Roles))
                    {
                        _permissionList = new List<string>
                        {
                            Constants.SettingsPermissions.Admin
                        };
                    }
                    else
                    {
                        _permissionList = _permissionsInRolesRepository.GetGeneralPermissionList(Roles);
                    }

                    DataCacheManager.InsertMinutes(_permissionListKey, _permissionList, 30);
                }
                return _permissionList ?? (_permissionList = new List<string>());
            }
        }

        private IList<string> Roles
        {
            get
            {
                if (_roles != null) return _roles;
                if (_adminInfo == null || _adminInfo.Locked)
                    return new List<string> { EPredefinedRoleUtils.GetValue(EPredefinedRole.Administrator) };

                _roles = DataCacheManager.Get<List<string>>(_rolesKey);
                if (_roles == null)
                {
                    _roles = _administratorsInRolesRepository.GetRolesForUser(_adminInfo.UserName).ToList();
                    DataCacheManager.InsertMinutes(_rolesKey, _roles, 30);
                }

                return _roles ?? new List<string> { EPredefinedRoleUtils.GetValue(EPredefinedRole.Administrator) };
            }
        }

        private Dictionary<int, List<string>> WebsitePermissionDict
        {
            get
            {
                if (_websitePermissionDict != null) return _websitePermissionDict;
                if (_adminInfo == null || _adminInfo.Locked) return new Dictionary<int, List<string>>();

                _websitePermissionDict = DataCacheManager.Get<Dictionary<int, List<string>>>(_websitePermissionDictKey);

                if (_websitePermissionDict == null)
                {
                    if (IsSystemAdministrator)
                    {
                        var allWebsitePermissionList = new List<string>();
                        foreach (var permission in WebsitePermissions)
                        {
                            allWebsitePermissionList.Add(permission.Id);
                        }

                        var siteIdList = GetSiteIdList();

                        _websitePermissionDict = new Dictionary<int, List<string>>();
                        foreach (var siteId in siteIdList)
                        {
                            _websitePermissionDict[siteId] = allWebsitePermissionList;
                        }
                    }
                    else
                    {
                        _websitePermissionDict = _sitePermissionsRepository.GetWebsitePermissionSortedList(Roles);
                    }
                    DataCacheManager.InsertMinutes(_websitePermissionDictKey, _websitePermissionDict, 30);
                }
                return _websitePermissionDict ?? (_websitePermissionDict = new Dictionary<int, List<string>>());
            }
        }

        private Dictionary<string, List<string>> ChannelPermissionDict
        {
            get
            {
                if (_channelPermissionDict != null) return _channelPermissionDict;
                if (_adminInfo == null || _adminInfo.Locked) return new Dictionary<string, List<string>>();

                _channelPermissionDict = DataCacheManager.Get<Dictionary<string, List<string>>>(_channelPermissionDictKey);

                if (_channelPermissionDict == null)
                {
                    if (EPredefinedRoleUtils.IsSystemAdministrator(Roles))
                    {
                        var allChannelPermissionList = new List<string>();
                        foreach (var permission in ChannelPermissions)
                        {
                            allChannelPermissionList.Add(permission.Id);
                        }

                        _channelPermissionDict = new Dictionary<string, List<string>>();

                        var siteIdList = GetSiteIdList();

                        foreach (var siteId in siteIdList)
                        {
                            _channelPermissionDict[GetChannelPermissionDictKey(siteId, siteId)] = allChannelPermissionList;
                        }
                    }
                    else
                    {
                        _channelPermissionDict = _sitePermissionsRepository.GetChannelPermissionSortedList(Roles);
                    }
                    DataCacheManager.InsertMinutes(_channelPermissionDictKey, _channelPermissionDict, 30);
                }

                return _channelPermissionDict ?? (_channelPermissionDict = new Dictionary<string, List<string>>());
            }
        }

        private List<string> ChannelPermissionListIgnoreChannelId
        {
            get
            {
                if (_channelPermissionListIgnoreChannelId != null) return _channelPermissionListIgnoreChannelId;
                if (_adminInfo == null || _adminInfo.Locked) return new List<string>();

                _channelPermissionListIgnoreChannelId = DataCacheManager.Get<List<string>>(_channelPermissionListIgnoreChannelIdKey);
                if (_channelPermissionListIgnoreChannelId == null)
                {
                    if (EPredefinedRoleUtils.IsSystemAdministrator(Roles))
                    {
                        _channelPermissionListIgnoreChannelId = new List<string>();
                        foreach (var permission in ChannelPermissions)
                        {
                            _channelPermissionListIgnoreChannelId.Add(permission.Id);
                        }
                    }
                    else
                    {
                        _channelPermissionListIgnoreChannelId = _sitePermissionsRepository.GetChannelPermissionListIgnoreChannelId(Roles);
                    }
                    DataCacheManager.InsertMinutes(_channelPermissionListIgnoreChannelIdKey, _channelPermissionListIgnoreChannelId, 30);
                }

                return _channelPermissionListIgnoreChannelId ?? (_channelPermissionListIgnoreChannelId = new List<string>());
            }
        }

        public bool HasSitePermissions(int siteId)
        {
            return IsSystemAdministrator || WebsitePermissionDict.ContainsKey(siteId);
        }

        public List<string> GetSitePermissions(int siteId)
        {
            return WebsitePermissionDict.TryGetValue(siteId, out var list) ? list : new List<string>();
        }

        private bool HasChannelPermissions(List<string> channelPermissionList, params string[] channelPermissions)
        {
            if (IsSystemAdministrator)
            {
                return true;
            }
            foreach (var channelPermission in channelPermissions)
            {
                if (channelPermissionList.Contains(channelPermission))
                {
                    return true;
                }
            }
            return false;
        }

        public bool HasChannelPermissions(int siteId, int channelId)
        {
            if (channelId == 0) return false;
            if (IsSystemAdministrator)
            {
                return true;
            }
            var dictKey = GetChannelPermissionDictKey(siteId, channelId);
            if (ChannelPermissionDict.ContainsKey(dictKey))
            {
                return true;
            }

            var parentChannelId = ChannelManager.GetParentId(siteId, channelId);
            return HasChannelPermissions(siteId, parentChannelId);
        }

        public List<string> GetChannelPermissions(int siteId, int channelId)
        {
            var dictKey = GetChannelPermissionDictKey(siteId, channelId);
            return ChannelPermissionDict.TryGetValue(dictKey, out var list) ? list : new List<string>();
        }

        public List<string> GetChannelPermissions(int siteId)
        {
            var list = new List<string>();
            foreach (var dictKey in ChannelPermissionDict.Keys)
            {
                var kvp = ParseChannelPermissionDictKey(dictKey);
                if (kvp.Key == siteId)
                {
                    foreach (var permission in ChannelPermissionDict[dictKey])
                    {
                        if (!list.Contains(permission))
                        {
                            list.Add(permission);
                        }
                    }
                }
            }

            return list;
        }

        public bool HasChannelPermissionsIgnoreChannelId(params string[] channelPermissions)
        {
            if (IsSystemAdministrator)
            {
                return true;
            }
            if (HasChannelPermissions(ChannelPermissionListIgnoreChannelId, channelPermissions))
            {
                return true;
            }
            return false;
        }

        public bool IsOwningChannelId(int channelId)
        {
            if (IsSystemAdministrator)
            {
                return true;
            }
            if (ChannelIdList.Contains(channelId))
            {
                return true;
            }
            return false;
        }

        public bool IsDescendantOwningChannelId(int siteId, int channelId)
        {
            if (IsSystemAdministrator)
            {
                return true;
            }

            var channelInfo = ChannelManager.GetChannelInfo(siteId, channelId);
            var channelIdList = ChannelManager.GetChannelIdList(channelInfo, ScopeType.Descendant, string.Empty, string.Empty, string.Empty);
            foreach (var theChannelId in channelIdList)
            {
                if (IsOwningChannelId(theChannelId))
                {
                    return true;
                }
            }
            return false;
        }

        public int? GetOnlyAdminId(int siteId, int channelId)
        {
            if (!_settingsManager.ConfigInfo.IsViewContentOnlySelf
                || IsConsoleAdministrator
                || IsSystemAdministrator
                || HasChannelPermissions(siteId, channelId, Constants.ChannelPermissions.ContentCheck))
            {
                return null;
            }
            return _adminInfo.Id;
        }

        public static string GetChannelPermissionDictKey(int siteId, int channelId)
        {
            return $"{siteId}_{channelId}";
        }

        private static KeyValuePair<int, int> ParseChannelPermissionDictKey(string dictKey)
        {
            if (string.IsNullOrEmpty(dictKey) || dictKey.IndexOf("_", StringComparison.Ordinal) == -1) return new KeyValuePair<int, int>(0, 0);

            return new KeyValuePair<int, int>(TranslateUtils.ToInt(dictKey.Split('_')[0]), TranslateUtils.ToInt(dictKey.Split('_')[1]));
        }

        private static string GetRolesCacheKey(string userName)
        {
            return DataCacheManager.GetCacheKey(nameof(Permissions), nameof(GetRolesCacheKey), userName);
        }

        private static string GetPermissionListCacheKey(string userName)
        {
            return DataCacheManager.GetCacheKey(nameof(Permissions), nameof(GetPermissionListCacheKey), userName);
        }

        private static string GetWebsitePermissionDictCacheKey(string userName)
        {
            return DataCacheManager.GetCacheKey(nameof(Permissions), nameof(GetWebsitePermissionDictCacheKey), userName);
        }

        private static string GetChannelPermissionDictCacheKey(string userName)
        {
            return DataCacheManager.GetCacheKey(nameof(Permissions), nameof(GetChannelPermissionDictCacheKey), userName);
        }

        private static string GetChannelPermissionListIgnoreChannelIdCacheKey(string userName)
        {
            return DataCacheManager.GetCacheKey(nameof(Permissions), nameof(GetChannelPermissionListIgnoreChannelIdCacheKey), userName);
        }

        private static string GetChannelIdListCacheKey(string userName)
        {
            return DataCacheManager.GetCacheKey(nameof(Permissions), nameof(GetChannelIdListCacheKey), userName);
        }

        public static void ClearAllCache()
        {
            DataCacheManager.RemoveByClassName(nameof(Permissions));
        }

        public List<Permission> GeneralPermissions { get; }

        public List<Permission> WebsitePermissions { get; }

        public List<Permission> WebsiteSysPermissions { get; }

        public List<Permission> WebsitePluginPermissions { get; }

        public List<Permission> ChannelPermissions { get; }
    }
}