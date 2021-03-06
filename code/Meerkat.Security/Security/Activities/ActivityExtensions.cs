﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

using Meerkat.Caching;
using Meerkat.Security.Activities.Configuration;

namespace Meerkat.Security.Activities
{
    /// <summary>
    /// Extensions methods arounds <see cref="Activity"/>
    /// </summary>
    public static class ActivityExtensions
    {
        /// <summary>
        /// Asynchronous lazy strongly-typed version of AddOrGetExisting which only invokes the function if the value is not present, 
        /// and returns either the cache value or the newly created value.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="cache"></param>
        /// <param name="key"></param>
        /// <param name="creator"></param>
        /// <param name="absoluteExpiration"></param>
        /// <param name="regionName"></param>
        /// <returns>Returns either the value that exists or the value returns from the creator function</returns>
        internal static async Task<T> AddOrGetExistingAsync<T>(this ICache cache, string key, Func<Task<T>> creator, DateTimeOffset absoluteExpiration, string regionName = null)
        {
            T value;
            if (cache.Contains(key, regionName))
            {
                value = (T)cache.Get(key, regionName);
            }
            else
            {
                value = await creator().ConfigureAwait(false);
                cache.Set(key, value, absoluteExpiration, regionName);
            }
            return value;
        }

        /// <summary>
        /// Convert some <see cref="Activity"/> into a dictionary so we can search them
        /// </summary>
        /// <param name="activities"></param>
        /// <returns></returns>
        public static IDictionary<string, Activity> ToDictionary(this IEnumerable<Activity> activities)
        {
            var dictionary = new Dictionary<string, Activity>();
            foreach (var activity in activities)
            {
                dictionary[activity.Name] = activity;
            }

            return dictionary;
        }

        /// <summary>
        /// Convert an <see cref="ActivityAuthorizationSection"/> into a <see cref="AuthorizationScope"/>
        /// </summary>
        /// <param name="authorizationSection"></param>
        /// <returns></returns>
        public static AuthorizationScope ToAuthorizationScope(this ActivityAuthorizationSection authorizationSection)
        {
            var scope = new AuthorizationScope();
            if (authorizationSection == null)
            {
                return scope;
            }

            var activities = new List<Activity>();
            for (var i = 0; i < authorizationSection.Activities.Count; i++)
            {
                var element = authorizationSection.Activities[i];
                activities.Add(element.ToActivity());
            }

            scope.Name = authorizationSection.Name;
            scope.DefaultAuthorization = authorizationSection.Default;
            scope.DefaultActivity = authorizationSection.DefaultActivity;
            scope.AllowUnauthenticated = authorizationSection.DefaultAllowUnauthenticated;
            scope.Activities = activities;

            return scope;
        }

        /// <summary>
        /// Convert an <see cref="ActivityElement"/> into an <see cref="Activity"/>.
        /// </summary>
        /// <param name="element"></param>
        /// <returns></returns>
        public static Activity ToActivity(this ActivityElement element)
        {
            var parts = element.Name.Split('.');
            var resource = string.IsNullOrEmpty(element.Resource) ? parts[0] : element.Resource;
            var action = string.IsNullOrEmpty(element.Action) ? (parts.Length > 1 ? parts[1] : string.Empty) : element.Action;

            var activity = new Activity
            {
                Resource = resource,
                Action = action,
                Default = element.Default,
                AllowUnauthenticated = element.AllowUnauthenticated,
                Allow = element.Allow.ToPermission(),
                Deny = element.Deny.ToPermission()
            };
            return activity;
        }

        /// <summary>
        /// Convert a <see cref="PermissionElement"/> to a <see cref="Permission"/>.
        /// </summary>
        /// <param name="element"></param>
        /// <returns></returns>
        public static Permission ToPermission(this PermissionElement element)
        {
            var permission = new Permission();

            if (!string.IsNullOrEmpty(element.Users))
            {
                permission.Users = element.Users.Split(',').Select(x => x.Trim()).ToList();
            }

            if (!string.IsNullOrEmpty(element.Roles))
            {
                permission.Roles = element.Roles.Split(',').Select(x => x.Trim()).ToList();
            }

            foreach (ClaimElement claimElement in element.Claims)
            {
                if (!string.IsNullOrEmpty(claimElement.Claims))
                {
                    foreach (var value in claimElement.Claims.Split(','))
                    {
                        var claim = new Claim(claimElement.Name.Trim(), value.Trim(), null, claimElement.Issuer.Trim());
                        permission.Claims.Add(claim);
                    }
                }
            }

            return permission;
        }

        /// <summary>
        /// Find all activities that have been modelled.
        /// </summary>
        /// <param name="activities"></param>
        /// <param name="resource"></param>
        /// <param name="action"></param>
        /// <param name="defaultActivity"></param>
        /// <returns></returns>
        public static IEnumerable<Activity> FindActivities(this IDictionary<string, Activity> activities, string resource, string action, string defaultActivity)
        {
            Activity value;

            // Find the closest activity match - resource centric
            foreach (var activity in Activities(resource, action))
            {
                if (activities.TryGetValue(activity, out value))
                {
                    yield return value;
                }
            }

            // Attempt to get the default activity.
            if (!string.IsNullOrEmpty(defaultActivity) && activities.TryGetValue(defaultActivity, out value))
            {
                yield return value;
            }
        }

        /// <summary>
        /// Determine the possible activity matches for a resource/action combination.
        /// <para>
        /// The order of the activities returns is important as it is from most-specific to least-specific
        /// and so 
        /// </para>
        /// </summary>
        /// <param name="resource"></param>
        /// <param name="action"></param>
        /// <returns></returns>
        public static IEnumerable<string> Activities(string resource, string action)
        {
            while (true)
            {
                if (string.IsNullOrEmpty(resource) && string.IsNullOrEmpty(action))
                {
                    // Base case, nothing else to return
                    yield break;
                }

                // Ok give the simple combination
                yield return ActivityName(resource, action);

                // Walk back the actions first
                foreach (var value in ActionActivities(resource, action))
                {
                    yield return value;
                }

                if (string.IsNullOrEmpty(resource))
                {
                    // Nothing more to do
                    yield break;
                }

                // Now check for a resource hierarchy
                var posn2 = resource.LastIndexOf('/');
                resource = posn2 == -1 ? string.Empty : resource.Substring(0, posn2);
            }
        }

        /// <summary>
        /// Determine the possible activity matches for a resource/action combination.
        /// </summary>
        /// <param name="resource"></param>
        /// <param name="action"></param>
        /// <returns></returns>
        private static IEnumerable<string> ActionActivities(string resource, string action)
        {
            while (!string.IsNullOrEmpty(action))
            {
                var posn = action.LastIndexOf('/');
                if (posn == -1)
                {
                    // Allow for naked resource
                    if (!string.IsNullOrEmpty(resource))
                    {
                        yield return resource;
                    }
                    break;
                }

                action = action.Substring(0, posn);
                yield return ActivityName(resource, action);
            }
        }

        private static string ActivityName(string resource, string action)
        {
            return string.IsNullOrEmpty(action) ? resource : string.Join(".", resource, action);
        }
    }
}