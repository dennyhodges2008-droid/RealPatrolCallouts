using System;
using System.Reflection;
using LSPD_First_Response.Mod.API;
using Rage;

namespace RealPatrolCallouts.Tasks
{
    /// <summary>
    /// Best-effort identity lookup for representing a driver's physical license during an
    /// interview. This deliberately stops at name/DOB - it never touches warrant, wanted,
    /// license-status, or criminal-history APIs. The player runs the collected identity
    /// manually through their own MDT later; this class only reads what the LSPDFR persona
    /// system has already attached to the ped so we can show it back to the player.
    ///
    /// Property access goes through reflection rather than a strongly-typed Persona
    /// reference so a minor field-name difference between LSPDFR API versions degrades
    /// gracefully (falls back to "Unknown Driver" / no DOB) instead of breaking the build.
    /// </summary>
    public static class PersonaHelper
    {
        public static string GetDisplayName(Ped ped)
        {
            try
            {
                object persona = Functions.GetPersonaForPed(ped);
                if (persona == null)
                {
                    return "Unknown Driver";
                }

                string forename = GetStringMember(persona, "Forename");
                string surname = GetStringMember(persona, "Surname");

                string fullName = (forename + " " + surname).Trim();
                return string.IsNullOrWhiteSpace(fullName) ? "Unknown Driver" : fullName;
            }
            catch (Exception)
            {
                return "Unknown Driver";
            }
        }

        /// <summary>Returns a formatted date of birth, or null if it isn't reliably available.</summary>
        public static string GetDateOfBirthText(Ped ped)
        {
            try
            {
                object persona = Functions.GetPersonaForPed(ped);
                if (persona == null)
                {
                    return null;
                }

                object birthday = GetMember(persona, "Birthday");
                if (birthday is DateTime dob)
                {
                    return dob.ToString("MM/dd/yyyy");
                }

                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string GetStringMember(object instance, string memberName)
        {
            return GetMember(instance, memberName) as string ?? string.Empty;
        }

        private static object GetMember(object instance, string memberName)
        {
            Type type = instance.GetType();

            PropertyInfo property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance);
            if (property != null)
            {
                return property.GetValue(instance);
            }

            FieldInfo field = type.GetField(memberName, BindingFlags.Public | BindingFlags.Instance);
            return field?.GetValue(instance);
        }
    }
}
