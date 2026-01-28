using System;
using UnityEditor;
using UnityEngine;

namespace Poiyomi.Pro
{
    /// <summary>
    /// Authentication is handled entirely by the website (pro.poiyomi.com).
    /// No local caching of tokens or credentials is performed.
    /// The website maintains session state and Patreon authentication.
    /// </summary>
    public static class PoiyomiProAuth
    {
        // All authentication is handled server-side at pro.poiyomi.com
        // No local token storage is needed or used
    }
}
