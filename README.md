# Temperator

Les températures et l'utilisation de ton PC, toujours à l'écran : un petit widget discret en haut à droite.

<p align="center"><img src="docs/widget-jauges.png" width="460" alt="Le widget Temperator en jauges rondes"></p>

- **CPU, GPU, RAM, disques, ventilateur** : température, utilisation, mise à jour chaque seconde.
- **Couleurs** : turquoise quand tout va bien, orange quand ça chauffe, rouge quand c'est trop chaud.
- **Flammes quand c'est trop chaud** (CPU ≥ 85 °C, GPU ≥ 87 °C, disque ≥ 60 °C) : en jauges rondes, seule la jauge concernée
  prend feu et rougeoie ; en lignes, c'est tout le bas du widget.
- **Mode translucide** : un verre teinté qui regarde le fond de l'écran autour de lui. Il passe en couleurs claires ou sombres
  pour rester lisible, prend la teinte du fond et s'opacifie si le fond est chargé. Ça s'adapte même pendant que tu le déplaces.
- **Jauges rondes** (affichage par défaut) : une tuile par composant, avec un anneau qui se remplit selon la température
  (turquoise → orange → rouge), la température au centre et l'utilisation juste en dessous.
  L'affichage en **lignes et barres** reste disponible (clic droit → *Affichage*).
- **Opacité du fond réglable à part** (curseur de 0 à 100 %), indépendante de l'opacité générale. Dès que le fond laisse voir
  l'écran, les couleurs s'adaptent automatiquement à ce qu'il y a derrière.
- **Sans aucun fond (100 % transparent)** : seuls le texte et les barres flottent sur l'écran. Les couleurs s'adaptent
  au fond de la même façon, avec un halo autour du texte pour qu'il reste lisible.
- **Au-dessus de tout, quoi qu'il arrive** : si une fenêtre d'une autre application passe devant le widget, même une autre
  fenêtre « toujours au premier plan », il repasse au-dessus. S'il est réduit, il revient.
- **Glisser** pour le déplacer. La position est retenue.
- **Double-clic** pour le *Plan du PC* : tout le matériel détecté et tous ses capteurs, en direct.
- **Clic droit** pour les réglages : affichage (lignes ou jauges), lignes affichées, taille, opacité générale, opacité du fond, fréquence, °C/°F, fond (opaque, translucide ou aucun), flammes, premier plan, verrouillage…
- **Icône près de l'horloge** : un clic masque ou réaffiche le widget, et le clic droit ouvre le même menu.
- Démarre avec Windows, **sans demande d'autorisation à chaque fois**.

<p align="center"><img src="docs/widget.png" width="300" alt="Affichage en lignes et barres"></p>
<p align="center"><sub>Affichage en lignes et barres</sub></p>
<p align="center">
  <img src="docs/widget-feu.png" width="260" alt="Trop chaud : le widget prend feu">
  <img src="docs/widget-translucide-sombre.png" width="260" alt="Mode translucide sur fond sombre">
  <img src="docs/widget-translucide-clair.png" width="260" alt="Mode translucide sur fond clair">
</p>
<p align="center"><sub>Trop chaud · translucide sur fond sombre · translucide sur fond clair</sub></p>
<p align="center">
  <img src="docs/widget-transparent-sombre.png" width="260" alt="Sans fond, sur fond sombre">
  <img src="docs/widget-transparent-clair.png" width="260" alt="Sans fond, sur fond clair">
</p>
<p align="center"><sub>Sans aucun fond : sur fond sombre · sur fond clair</sub></p>
<p align="center">
  <img src="docs/widget-jauges.png" width="420" alt="Jauges rondes"><br>
  <img src="docs/widget-jauges-feu.png" width="350" alt="Jauges rondes, trop chaud">
  <img src="docs/widget-jauges-fond-40.png" width="420" alt="Jauges rondes, fond à 40 %">
</p>
<p align="center"><sub>Jauges rondes · trop chaud (seule la jauge du CPU brûle) · fond à 40 %</sub></p>

> Ce que Windows ne permet pas de recouvrir : le menu Démarrer, la recherche, Alt+Tab, le centre de notifications
> et les jeux en plein écran *exclusif*. Les jeux en mode « fenêtré sans bordure » fonctionnent.

## Installation

1. Télécharge **`Temperator-Setup-x.y.z.exe`** depuis la page [Releases](https://github.com/Buntasam/Temperator/releases/latest).
2. Lance-le et accepte la demande d'administrateur.
3. Choisis tes options, puis clique sur **Installer**.

<p align="center"><img src="docs/installeur.png" width="460" alt="L'installeur"></p>

L'installeur ajoute aussi ce qui manque :

| Composant | Pourquoi | Taille |
|---|---|---|
| [.NET 10 Desktop Runtime](https://dotnet.microsoft.com) (Microsoft) | fait tourner Temperator | environ 57 Mo |
| Pilote [PawnIO](https://pawnio.eu) (open source, signé) | température du CPU et ventilateurs | environ 3 Mo |

> **« Windows a protégé votre ordinateur » ?** L'installeur n'est pas signé numériquement, donc SmartScreen
> peut l'afficher. Clique sur *Informations complémentaires*, puis sur *Exécuter quand même*.
> L'empreinte SHA-256 de chaque version est publiée avec la release.

**Désinstaller** : *Paramètres → Applications → Temperator*, ou relance l'installeur et choisis *Désinstaller*.
.NET et PawnIO restent installés, car d'autres logiciels peuvent s'en servir.

**Installation silencieuse** : `Temperator-Setup-x.y.z.exe /S`. Options possibles : `-NoAutoStart -NoDesktop -NoLaunch`.
Le journal est écrit dans `%TEMP%\temperator-setup.log`.

## Plan du PC

<p align="center"><img src="docs/plan-du-pc.png" width="520" alt="Plan du PC"></p>

## Pour les développeurs

Il faut Windows 10/11 64 bits et le [SDK .NET 10](https://dotnet.microsoft.com/download/dotnet/10.0) (`winget install Microsoft.DotNet.SDK.10`).

```powershell
# lancer le widget (Debug : sans élévation, donc sans température CPU)
dotnet run --project src/Temperator

# construire l'installeur : dist\Temperator-Setup-<version>.exe
powershell -ExecutionPolicy Bypass -File build.ps1
```

| Dossier | Contenu |
|---|---|
| `src/Temperator` | le widget (WPF, .NET 10, [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)) |
| `installer/Installer.ps1` | l'installeur (interface WPF dans Windows PowerShell) |
| `src/Setup` | `Temperator-Setup.exe`, qui contient l'installeur et l'application |
| `tools/make-icon.ps1` | génère l'icône |

Captures d'écran : `Temperator.exe --snapshot <dossier>` (ajoute `--demo` pour des valeurs d'exemple),
et `Installer.ps1 -Preview <dossier>` pour l'installeur.

Les réglages sont dans `%ProgramData%\Temperator\settings.json`, et les erreurs dans `erreurs.log` au même endroit.

## Licences

Les composants tiers et leurs licences sont listés dans [LICENCES-TIERS.txt](LICENCES-TIERS.txt).
