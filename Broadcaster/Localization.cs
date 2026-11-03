using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Forms;

namespace Broadcaster
{
    /// <summary>
    /// Textes de l'interface utilisateur. Les identifiants sont les textes français historiques,
    /// ce qui permet de conserver tous les contrôles existants sans ressources satellites.
    /// Pour ajouter une langue, compléter les traductions, LanguageCodes et ResolveLanguage.
    /// </summary>
    internal static partial class Localization
    {
        private static string _language = "en";
        public static string ActiveLanguage => _language;

        // Langues disponibles et ordre du menu des paramètres.
        // Le norvégien est proposé en bokmål; nb/no/nn de Windows y sont associés.
        internal static readonly string[] LanguageCodes =
            { "auto", "fr", "en", "es", "de", "nb", "sv", "it", "pt" };

        internal static readonly string[] LanguageNames =
            { "Automatique (langue de Windows)", "Français", "English", "Español",
              "Deutsch", "Norsk (bokmål)", "Svenska", "Italiano", "Português" };

        public static string NormalizeMode(string mode)
        {
            if (string.IsNullOrWhiteSpace(mode)) return "auto";
            string normalized = mode.Trim().ToLowerInvariant();
            if (normalized == "no" || normalized == "nn") normalized = "nb";
            foreach (string code in LanguageCodes)
                if (code == normalized) return code;
            return "auto";
        }

        public static string ResolveLanguage(string mode, CultureInfo systemUiCulture = null)
        {
            mode = NormalizeMode(mode);
            if (mode != "auto") return mode;

            var culture = systemUiCulture ?? CultureInfo.CurrentUICulture;
            string language = culture.TwoLetterISOLanguageName.ToLowerInvariant();
            if (language == "no" || language == "nn") language = "nb";
            // Langue Windows non couverte : anglais, et non français.
            foreach (string code in LanguageCodes)
                if (code == language) return code;
            return "en";
        }

        public static void UseLanguage(string mode) => _language = ResolveLanguage(mode);

        public static string T(string french)
        {
            if (string.IsNullOrEmpty(french)) return french;
            if (_language == "fr")
                return FrenchTechnicalChoices.TryGetValue(french, out string uiTerm) ? uiTerm : french;
            Dictionary<string, string> translations;
            switch (_language)
            {
                case "es": translations = Spanish; break;
                case "de": translations = German; break;
                case "nb": translations = NorwegianBokmal; break;
                case "sv": translations = Swedish; break;
                case "it": translations = Italian; break;
                case "pt": translations = Portuguese; break;
                default: translations = English; break;
            }
            if (translations.TryGetValue(french, out string translated)) return translated;
            if (FeatureTranslations.TryGetValue(_language, out var featureLanguage) &&
                featureLanguage.TryGetValue(french, out translated)) return translated;
            // Consistent English fallback when a new translation is missing.
            if (English.TryGetValue(french, out translated)) return translated;
            if (FeatureTranslations["en"].TryGetValue(french, out translated)) return translated;
            return french;
        }

        public static string F(string frenchFormat, params object[] args) =>
            string.Format(CultureInfo.CurrentCulture, T(frenchFormat), args);

        // Ne jamais traduire les champs de saisie ni les éléments ComboBox liés au matériel :
        // on traduit uniquement les textes statiques des contrôles WinForms.
        public static void TranslateControls(Control control)
        {
            if (control is Form || control is Label || control is Button ||
                control is CheckBox || control is TabPage || control is GroupBox ||
                control is RadioButton)
                control.Text = T(control.Text);
            foreach (Control child in control.Controls) TranslateControls(child);
        }

        private static readonly Dictionary<string, string> English = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Options / General
            ["Général"] = "General",
            ["Vidéo"] = "Video",
            ["Extras"] = "Extras",
            ["Options - Broadcaster"] = "Broadcaster - Settings",
            ["Écran (plein écran) :"] = "Screen (fullscreen):",
            ["Résolution :"] = "Resolution:",
            ["Volume :"] = "Volume:",
            ["Source de capture vidéo :"] = "Video capture source:",
            ["Capture audio (si distincte de la vidéo)"] = "Audio capture (if separate from video):",
            ["Sortie audio :"] = "Audio output:",
            ["Langue de l'application :"] = "Application language:",
            ["Écran"] = "Display",
            [" (Principal)"] = " (Primary)",
            [" (personnalisé)"] = " (custom)",
            ["(Aucune - le device vidéo fournit déjà l'audio)"] = "(None - video device already provides audio)",
            ["(Périphérique par défaut du système)"] = "(System default device)",
            ["Automatique (langue de Windows)"] = "Automatic (Windows language)",
            // Extras
            ["Relancer automatiquement la capture 3 s après le démarrage (corrige le décalage du son)"] = "Automatically restart capture 3 s after startup (fixes audio delay)",
            ["Raccourcis clavier..."] = "Keyboard shortcuts...",
            ["Rechercher une mise à jour"] = "Check for updates",
            ["Installer la mise à jour"] = "Install update",
            ["Ouvrir le dossier des captures"] = "Open screenshots folder",
            ["Activer les clips des dernières secondes"] = "Enable instant replay clips",
            ["Durée du clip (secondes) :"] = "Clip duration (seconds):",
            ["FPS cible du clip :"] = "Target clip FPS:",
            ["AVI MJPEG à la résolution choisie dans Général (jusqu'à 4K).\nFPS cible : selon les performances du PC ; les grands clips utilisent de la RAM.\nLa fenêtre doit rester visible et non recouverte (son Windows si disponible)."] =
                "MJPEG AVI at the resolution selected in General (up to 4K).\n" +
                "Target FPS depends on PC performance; large clips use RAM.\n" +
                "Keep the window visible and unobstructed (Windows audio if available).",
            ["Ouvrir le dossier des clips"] = "Open clips folder",
            ["Version actuelle : {0}"] = "Current version: {0}",
            ["Recherche en cours..."] = "Checking for updates...",
            ["Application à jour (version {0})."] = "Up to date (version {0}).",
            ["Version {0} disponible, mais aucune archive ZIP n'est jointe à la release."] = "Version {0} is available, but no ZIP asset is attached to the release.",
            ["Nouvelle version {0} disponible ! (actuelle : {1})"] = "New version {0} available! (current: {1})",
            ["Mettre à jour vers {0}"] = "Update to {0}",
            ["Impossible de vérifier : {0}"] = "Could not check for updates: {0}",
            ["Téléchargement de la mise à jour..."] = "Downloading update...",
            ["Téléchargement : {0}%"] = "Downloading: {0}%",
            ["Téléchargement terminé. Fermeture pour installation..."] = "Download complete. Closing to install...",
            ["Mise à jour impossible : {0}"] = "Update failed: {0}",
            ["Mise à jour de Broadcaster"] = "Broadcaster update",
            ["Installer Broadcaster {0} depuis GitHub ?\n\nFichier : {1}\n\nL'application se fermera, ses fichiers seront remplacés, puis elle redémarrera. Les paramètres utilisateur seront conservés."] =
                "Install Broadcaster {0} from GitHub?\n\nFile: {1}\n\nThe application will close, replace its files, then restart. User settings will be preserved.",
            ["La mise à jour n'a pas été installée :\n{0}"] = "The update was not installed:\n{0}",
            ["Impossible d'ouvrir le dossier des clips : {0}"] = "Could not open clips folder: {0}",
            ["Impossible d'ouvrir le dossier des captures : {0}"] = "Could not open screenshots folder: {0}",
            // Video / profiles
            ["Profil :"] = "Profile:",
            ["Par défaut"] = "Default",
            ["Nouveau profil..."] = "New profile...",
            ["Renommer..."] = "Rename...",
            ["Supprimer"] = "Delete",
            ["Luminosité :"] = "Brightness:",
            ["Contraste :"] = "Contrast:",
            ["Teinte :"] = "Hue:",
            ["Saturation :"] = "Saturation:",
            ["Sélectionne une source vidéo et clique sur Appliquer une première fois pour pouvoir régler la couleur."] =
                "Select a video source and click Apply once before adjusting colors.",
            ["Afficher l'image en l'absence de signal (10s)"] = "Show the no-signal image after 10 s",
            ["Choisir une image..."] = "Choose image...",
            ["Retirer l'image"] = "Remove image",
            ["Appliquer"] = "Apply",
            ["Annuler"] = "Cancel",
            ["Supprimer le profil \"{0}\" ?"] = "Delete profile \"{0}\"?",
            ["Nouveau profil"] = "New profile",
            ["Nom du profil :"] = "Profile name:",
            ["Renommer le profil"] = "Rename profile",
            ["Nouveau nom :"] = "New name:",
            ["Un profil porte déjà ce nom."] = "A profile with that name already exists.",
            ["Aucune source vidéo sélectionnée."] = "No video source selected.",
            ["Impossible de charger cette image : {0}"] = "Could not load image: {0}",
            ["Choisir une image de fond"] = "Choose a background image",
            // Shortcuts
            ["Raccourcis clavier"] = "Keyboard shortcuts",
            ["Ouvrir le menu"] = "Open menu",
            ["Relancer la capture"] = "Restart capture",
            ["Afficher / masquer le curseur"] = "Show / hide cursor",
            ["Capture d'écran"] = "Screenshot",
            ["Enregistrer un clip"] = "Save replay clip",
            ["F11 (plein écran) reste fixe."] = "F11 (fullscreen) cannot be changed.",
            ["Réinitialiser"] = "Reset",
            ["Enregistrer"] = "Save",
            ["Modifier"] = "Change",
            ["Appuie sur une touche"] = "Press a key",
            ["F11 est réservée au plein écran."] = "F11 is reserved for fullscreen.",
            ["{0} est déjà utilisée pour « {1} »."] = "{0} is already assigned to '{1}'.",
            ["Échap"] = "Esc",
            ["Entrée"] = "Enter",
            ["Espace"] = "Space",
            ["Retour arrière"] = "Backspace",
            // Update installer validation errors
            ["Aucune mise à jour téléchargeable n'a été sélectionnée."] = "No downloadable update was selected.",
            ["L'adresse de téléchargement GitHub n'est pas reconnue."] = "Unrecognized GitHub download address.",
            ["Impossible de retrouver le dossier de l'application."] = "Could not locate the application folder.",
            ["Le programme de mise à jour n'a pas pu démarrer."] = "The updater could not start.",
            ["La release ZIP dépasse la limite autorisée de 512 Mo."] = "The release ZIP exceeds the 512 MB limit.",
            ["Le téléchargement du ZIP est incomplet."] = "The ZIP download is incomplete.",
            ["L'empreinte fournie par GitHub est invalide."] = "The checksum supplied by GitHub is invalid.",
            ["L'empreinte SHA-256 du ZIP ne correspond pas à celle de GitHub."] = "The ZIP's SHA-256 checksum does not match GitHub.",
            ["Le ZIP contient un nombre anormal de fichiers."] = "The ZIP contains an unusual number of files.",
            ["Chemin interdit dans le ZIP : {0}"] = "Forbidden path in ZIP: {0}",
            ["Le ZIP contient un lien symbolique non autorisé."] = "The ZIP contains a disallowed symbolic link.",
            ["Un chemin ZIP tente de sortir du dossier d'extraction."] = "A ZIP entry attempts to escape the extraction folder.",
            ["Entrée ZIP en double : {0}"] = "Duplicate ZIP entry: {0}",
            ["La taille décompressée dépasse 1 Go."] = "The extracted size exceeds 1 GB.",
            ["Le ZIP doit contenir exactement un fichier {0}."] = "The ZIP must contain exactly one {0} file.",
            ["La version de l'exécutable dans le ZIP ne correspond pas à la release GitHub."] = "The executable version in the ZIP does not match the GitHub release.",
            ["La release ne contient pas un exécutable .NET Broadcaster valide."] = "The release does not contain a valid .NET Broadcaster executable.",
            // Clip buffer error messages (diagnostics.log intentionally stays French)
            ["Résolution de clip non prise en charge (maximum 3840 x 2160)."] = "Unsupported clip resolution (maximum 3840 x 2160).",
            ["Impossible d'obtenir le bureau Windows."] = "Could not access the Windows desktop.",
            ["Impossible d'activer le redimensionnement de qualité."] = "Could not enable high-quality scaling.",
            ["Échec de la copie vidéo redimensionnée."] = "Failed to copy the scaled video frame.",
            ["Un clip est déjà en cours d'enregistrement."] = "A clip is already being saved.",
            ["Active les clips dans Options > Extras."] = "Enable clips in Settings > Extras.",
            ["La zone de capture vidéo n'est pas disponible."] = "The video capture region is unavailable.",
            ["La capture échoue : {0}"] = "Video capture failed: {0}",
            ["Le tampon n'a pas encore assez d'images."] = "Not enough frames have been buffered yet.",
            [" Vérifie que la vidéo est visible et attends quelques secondes. Images récentes : {0}."] =
                " Make sure the video is visible and wait a few seconds. Recent frames: {0}.",
            ["Résolution du tampon incorrecte : les images sont en {0}x{1} alors que les paramètres demandent {2}x{3}."] =
                "Wrong buffer resolution: frames are {0}x{1} while settings require {2}x{3}.",
            ["Le fichier AVI produit est vide ou incomplet."] = "The AVI file is empty or incomplete.",
            ["Le device vidéo n'a pas d'activateur valide. Ouvre les options et resélectionne-le dans la liste."] =
                "The video device has no valid activator. Open settings and select it again.",
            ["Erreur Media Foundation ({0}, code 0x{1:X8}) sur l'évènement {2}."] =
                "Media Foundation error ({0}, code 0x{1:X8}) during event {2}.",
            // Main window & errors
            ["Erreur de capture"] = "Capture error",
            ["Impossible d'enregistrer la capture d'écran : {0}"] = "Could not save screenshot: {0}",
            ["Le tampon de clips n'est pas démarré. Vérifie la capture vidéo et réessaie."] = "Replay buffer has not started. Check video capture and try again.",
            ["Impossible d'enregistrer le clip : {0}\n\nJournal : {1}"] = "Could not save clip: {0}\n\nLog: {1}",
            ["Le périphérique vidéo configuré est introuvable. Ouvre les options pour en choisir un autre."] =
                "The configured video device could not be found. Open settings to choose another.",
            ["Impossible de démarrer les clips : {0}"] = "Could not start replay clips: {0}",
            ["Impossible de démarrer la capture : {0}"] = "Could not start capture: {0}",
            ["Un clip est en cours d'écriture. Réessaie de fermer Broadcaster après le son de confirmation."] =
                "A clip is being saved. Try closing Broadcaster after the confirmation sound."
        };
    }
}
