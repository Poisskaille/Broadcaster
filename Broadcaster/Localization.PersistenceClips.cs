using System;
using System.Collections.Generic;

namespace Broadcaster
{
    internal static partial class Localization
    {
        // Nouveaux libelles de sauvegarde et clips, pour les 8 langues existantes.
        private static readonly Dictionary<string, Dictionary<string, string>> PersistenceClipTranslations =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
            {
                ["en"] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Qualité JPEG du clip :"] = "Clip JPEG quality:",
                    ["Impossible de sauvegarder la présence Discord :"] = "Could not save Discord Rich Presence:",
                    ["Impossible d'enregistrer les options :"] = "Could not save settings:"
                },
                ["es"] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Qualité JPEG du clip :"] = "Calidad JPEG del clip:",
                    ["Impossible de sauvegarder la présence Discord :"] = "No se pudo guardar la presencia en Discord:",
                    ["Impossible d'enregistrer les options :"] = "No se pudo guardar la configuración:"
                },
                ["de"] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Qualité JPEG du clip :"] = "JPEG-Qualität des Clips:",
                    ["Impossible de sauvegarder la présence Discord :"] = "Discord-Präsenz konnte nicht gespeichert werden:",
                    ["Impossible d'enregistrer les options :"] = "Einstellungen konnten nicht gespeichert werden:"
                },
                ["nb"] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Qualité JPEG du clip :"] = "JPEG-kvalitet for klipp:",
                    ["Impossible de sauvegarder la présence Discord :"] = "Kunne ikke lagre Discord-tilstedeværelse:",
                    ["Impossible d'enregistrer les options :"] = "Kunne ikke lagre innstillingene:"
                },
                ["sv"] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Qualité JPEG du clip :"] = "JPEG-kvalitet för klipp:",
                    ["Impossible de sauvegarder la présence Discord :"] = "Kunde inte spara Discord-närvaro:",
                    ["Impossible d'enregistrer les options :"] = "Kunde inte spara inställningarna:"
                },
                ["it"] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Qualité JPEG du clip :"] = "Qualità JPEG della clip:",
                    ["Impossible de sauvegarder la présence Discord :"] = "Impossibile salvare la presenza Discord:",
                    ["Impossible d'enregistrer les options :"] = "Impossibile salvare le impostazioni:"
                },
                ["pt"] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Qualité JPEG du clip :"] = "Qualidade JPEG do clipe:",
                    ["Impossible de sauvegarder la présence Discord :"] = "Não foi possível guardar a presença no Discord:",
                    ["Impossible d'enregistrer les options :"] = "Não foi possível guardar as definições:"
                }
            };
    }
}
