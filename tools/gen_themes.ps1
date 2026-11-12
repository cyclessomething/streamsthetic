# Writes windows/app/Theme/<Theme>-<Light|Dark>.xaml from the table below: the single source of the PC app's themes.
# Edit a value here, run this script, then tools\theme_selftest.ps1 (same keys everywhere, text contrast >= 4.5).
# The phone app has the same values by hand in android/.../Looks.kt: change both.
$out = Join-Path (Split-Path $PSScriptRoot -Parent) "windows\app\Theme"
$MONO = 'pack://application:,,,/Fonts/#IBM Plex Mono'; $SANS = 'pack://application:,,,/Fonts/#Archivo'
$DOTS = 'pack://application:,,,/Fonts/#Doto'; $CHAKRA = 'pack://application:,,,/Fonts/#Chakra Petch'; $ANTON = 'pack://application:,,,/Fonts/#Anton'; $POPPINS = 'pack://application:,,,/Fonts/#Poppins'; $MICHROMA = 'pack://application:,,,/Fonts/#Michroma'
$status = @{ Good='0F7A3A'; Bad='C8260E'; Wait='B45309'; Idle='6B6B6B'; OnStatus='FFFFFF' }
$base = @{
  PanelCorner='6'; PanelBorder='1'; ChipCorner='4'; ChipBorder='1'; ButtonCorner='6'; HeaderIndent='10,0,0,0'; PanelEffect=$null; PanelEffectLight=$null; ChipEffect=$null; TicksVisibility='Collapsed'
  LabelFont=$MONO; TitleFont=$MONO; ChipFont=$MONO; ReadoutFont=$SANS; WordmarkFont=$DOTS
  LabelSize='11'; TitleSize='12'; ReadoutSize='40'; ReadoutLine='42'; WordmarkSize='30'; TitleWeight='SemiBold'; ReadoutWeight='Bold'; UpperCase='True'
  AccentBarVisibility='Visible'; IconVisibility='Collapsed'; BracketVisibility='Collapsed'; ViewfinderVisibility='Visible'; StarVisibility='Collapsed'; ArrowVisibility='Collapsed'
  ArrowGlyph='2198'; Icon1='E839'; Icon2='E722'; Icon3='E7AD'; Icon4='E91B'; Icon5='E713'
}
function T($o) { $h = $base.Clone(); $status.GetEnumerator() | ForEach-Object { $h[$_.Key] = $_.Value }; $o.GetEnumerator() | ForEach-Object { $h[$_.Key] = $_.Value }; $h }

$themes = [ordered]@{}
$studioShape = @{}
$themes['Studio-Light'] = T @{ Paper='F4F2EC'; Panel='FCFBF8'; PanelStroke='E2DED6'; Hairline='E2DED6'; Ink='1B1D1F'; Ink2='6E6B67'; Ink3='A9A59F'; Accent='FE5921'; AccentHover='FF6E3C'; OnAccent='1B1D1F'; Link='C23C13'; Track='E7E3DB'; ChipHover='EFECE5'; ChipFill='00FFFFFF'; ChipStroke='E2DED6'; Primary='FE5921'; PrimaryHover='FF6E3C'; OnPrimary='1B1D1F'; HeaderBlock='FE5921'; OnHeader='1B1D1F'; Section1='FE5921'; Section2='FE5921'; Section3='FE5921'; Section4='FE5921'; Section5='FE5921'; SectionInk='1B1D1F' }
$themes['Studio-Dark'] = T @{ Paper='0F1011'; Panel='17191B'; PanelStroke='2A2C2F'; Hairline='2A2C2F'; Ink='F1F1F1'; Ink2='9C9A97'; Ink3='5E5C59'; Accent='FE5921'; AccentHover='FF6E3C'; OnAccent='1B1D1F'; Link='FF7A4D'; Track='2A2C2F'; ChipHover='222427'; ChipFill='00FFFFFF'; ChipStroke='2A2C2F'; Primary='FE5921'; PrimaryHover='FF6E3C'; OnPrimary='1B1D1F'; HeaderBlock='FE5921'; OnHeader='1B1D1F'; Section1='FE5921'; Section2='FE5921'; Section3='FE5921'; Section4='FE5921'; Section5='FE5921'; SectionInk='1B1D1F' }

$creatorShape = @{ PanelCorner='20'; PanelBorder='0'; ChipCorner='16'; ChipBorder='0'; ButtonCorner='24'; LabelFont=$SANS; TitleFont=$SANS; ChipFont=$SANS; ReadoutFont=$SANS; WordmarkFont=$SANS
  LabelSize='12.5'; TitleSize='15'; WordmarkSize='24'; TitleWeight='Bold'; UpperCase='False'; AccentBarVisibility='Collapsed'; IconVisibility='Visible'; ViewfinderVisibility='Collapsed' }
$themes['Creator-Light'] = T ($creatorShape + @{ Paper='F2F2F4'; Panel='FFFFFF'; PanelStroke='00FFFFFF'; Hairline='E5E5EA'; Ink='1C1C1E'; Ink2='5F6368'; Ink3='A1A1A6'; Accent='0F7A3A'; AccentHover='13903F'; OnAccent='FFFFFF'; Link='0F7A3A'; Track='E5E5EA'; ChipHover='E4E4E8'; ChipFill='EEEEF1'; ChipStroke='00FFFFFF'; Primary='0F7A3A'; PrimaryHover='13903F'; OnPrimary='FFFFFF'; HeaderBlock='FFFFFF'; OnHeader='1C1C1E'; Section1='1A6FE0'; Section2='0B76A8'; Section3='0F7A3A'; Section4='B45309'; Section5='6D3FD8'; SectionInk='FFFFFF'
  PanelEffect='<DropShadowEffect x:Key="PanelEffect" BlurRadius="24" ShadowDepth="6" Direction="270" Opacity="0.07" Color="#000000" />' })
$themes['Creator-Dark'] = T ($creatorShape + @{ Paper='111214'; Panel='1C1D20'; PanelStroke='2A2C30'; Hairline='2A2C30'; Ink='F2F2F4'; Ink2='A0A0A6'; Ink3='6B6B70'; Accent='34C759'; AccentHover='4AD66D'; OnAccent='0B1F12'; Link='34C759'; Track='2A2C30'; ChipHover='2E3035'; ChipFill='26272B'; ChipStroke='00FFFFFF'; Primary='34C759'; PrimaryHover='4AD66D'; OnPrimary='0B1F12'; HeaderBlock='1C1D20'; OnHeader='F2F2F4'; Section1='4C9AFF'; Section2='4CC2F0'; Section3='5BD67E'; Section4='FFB02E'; Section5='A78BFA'; SectionInk='0B0B0C' })
$themes['Creator-Dark'].PanelBorder = '1'

$themes['Gamer-Dark'] = T @{ Paper='0A0A0B'; Panel='111113'; PanelStroke='59FFE01B'; Hairline='2A2A2E'; Ink='F5F5F0'; Ink2='9C9A97'; Ink3='5E5C59'; Accent='FFE01B'; AccentHover='FFEA5C'; OnAccent='0A0A0B'; Link='FFE01B'; Track='2A2A2E'; ChipHover='24220D'; ChipFill='00FFFFFF'; ChipStroke='99FFE01B'; Primary='FFE01B'; PrimaryHover='FFEA5C'; OnPrimary='0A0A0B'; HeaderBlock='FFE01B'; OnHeader='0A0A0B'
  Section1='FFE01B'; Section2='FFE01B'; Section3='FFE01B'; Section4='FFE01B'; Section5='FFE01B'; SectionInk='0A0A0B'; Good='FF5A3C'; Bad='FF2E63'; Wait='FFE01B'; Idle='8A8A90'; OnStatus='0A0A0B'
  PanelCorner='0'; ChipCorner='0'; ButtonCorner='0'; TitleFont=$CHAKRA; ChipFont=$CHAKRA; ReadoutFont=$CHAKRA; WordmarkFont=$CHAKRA; TitleSize='13'; WordmarkSize='28'
  BracketVisibility='Visible'; StarVisibility='Visible'; ArrowVisibility='Visible' }

$brutalShape = @{ HeaderIndent='0'; PanelCorner='0'; PanelBorder='2'; ChipCorner='0'; ChipBorder='2'; ButtonCorner='0'; TitleFont=$ANTON; ReadoutFont=$ANTON; WordmarkFont=$ANTON; TitleSize='15'; ReadoutSize='46'; ReadoutLine='48'; TitleWeight='Normal'; ReadoutWeight='Normal'; AccentBarVisibility='Collapsed'; ViewfinderVisibility='Collapsed' }
$themes['Brutal-Light'] = T ($brutalShape + @{ Paper='E4E4E4'; Panel='ECECEC'; PanelStroke='1A1A1A'; Hairline='1A1A1A'; Ink='1A1A1A'; Ink2='4A4A4A'; Ink3='8A8A8A'; Accent='D92D14'; AccentHover='EA3B20'; OnAccent='FFFFFF'; Link='B8220C'; Track='C9C9C9'; ChipHover='DCDCDC'; ChipFill='00FFFFFF'; ChipStroke='1A1A1A'; Primary='1A1A1A'; PrimaryHover='D92D14'; OnPrimary='FFFFFF'; HeaderBlock='1A1A1A'; OnHeader='FFFFFF'; Section1='D92D14'; Section2='D92D14'; Section3='D92D14'; Section4='D92D14'; Section5='D92D14'; SectionInk='FFFFFF' })
$themes['Brutal-Dark'] = T ($brutalShape + @{ Paper='111111'; Panel='1A1A1A'; PanelStroke='E8E8E8'; Hairline='E8E8E8'; Ink='F1F1F1'; Ink2='B0B0B0'; Ink3='707070'; Accent='FF4A2B'; AccentHover='FF6247'; OnAccent='0F0F0F'; Link='FF4A2B'; Track='333333'; ChipHover='262626'; ChipFill='00FFFFFF'; ChipStroke='E8E8E8'; Primary='F1F1F1'; PrimaryHover='FF4A2B'; OnPrimary='111111'; HeaderBlock='F1F1F1'; OnHeader='111111'; Section1='FF4A2B'; Section2='FF4A2B'; Section3='FF4A2B'; Section4='FF4A2B'; Section5='FF4A2B'; SectionInk='0F0F0F' })

$dotShape = @{ HeaderIndent='0'; PanelCorner='14'; ChipCorner='16'; ButtonCorner='22'; TitleFont=$DOTS; ReadoutFont=$DOTS; WordmarkFont=$DOTS; TitleSize='17'; ReadoutSize='48'; ReadoutLine='50'; TitleWeight='Normal'; ReadoutWeight='Normal'; UpperCase='False'; AccentBarVisibility='Collapsed'; ViewfinderVisibility='Collapsed' }
$themes['Dot-Light'] = T ($dotShape + @{ Paper='F1F1F1'; Panel='F7F7F5'; PanelStroke='1F1B1D1F'; Hairline='D5D5D3'; Ink='1B1D1F'; Ink2='5A5A5A'; Ink3='9A9A9A'; Accent='D71921'; AccentHover='E8323A'; OnAccent='FFFFFF'; Link='D71921'; Track='DCDCDA'; ChipHover='E8E8E6'; ChipFill='00FFFFFF'; ChipStroke='661B1D1F'; Primary='D71921'; PrimaryHover='E8323A'; OnPrimary='FFFFFF'; HeaderBlock='D71921'; OnHeader='FFFFFF'; Section1='D71921'; Section2='D71921'; Section3='D71921'; Section4='D71921'; Section5='D71921'; SectionInk='FFFFFF' })
$themes['Dot-Dark'] = T ($dotShape + @{ Paper='1B1D1F'; Panel='232527'; PanelStroke='33F1F1F1'; Hairline='3A3C3F'; Ink='F1F1F1'; Ink2='A9A9A9'; Ink3='6E6E6E'; Accent='FF4B55'; AccentHover='FF6A72'; OnAccent='1B1D1F'; Link='FF4B55'; Track='3A3C3F'; ChipHover='2D2F32'; ChipFill='00FFFFFF'; ChipStroke='66F1F1F1'; Primary='FF4B55'; PrimaryHover='FF6A72'; OnPrimary='1B1D1F'; HeaderBlock='FF4B55'; OnHeader='1B1D1F'; Section1='FF4B55'; Section2='FF4B55'; Section3='FF4B55'; Section4='FF4B55'; Section5='FF4B55'; SectionInk='1B1D1F' })

# PRISM. Owner: "white, grey and a red accent, red very minimal, like the Nothing phone" + design language of the owner's
# "Metropulse" image (warm-modern mood): soft raised (neumorphic) surfaces with a light and a dark shadow, very large radius,
# no borders, wide thin lettering (Michroma), tick-mark scales on sliders. Red (D71921) appears only on slider fills, links,
# one big number and the header slash; chosen/pressed things are charcoal (light) or silver (dark), never red.
$prismShape = @{ PanelCorner='34'; PanelBorder='0'; ChipCorner='18'; ChipBorder='0'; ButtonCorner='30'; HeaderIndent='10,0,0,0'; LabelFont=$MICHROMA; TitleFont=$MICHROMA; ChipFont=$SANS; ReadoutFont=$MICHROMA; WordmarkFont=$MICHROMA
  LabelSize='9.5'; TitleSize='11'; ReadoutSize='34'; ReadoutLine='38'; WordmarkSize='20'; TitleWeight='Normal'; ReadoutWeight='Normal'; AccentBarVisibility='Collapsed'; IconVisibility='Visible'; ViewfinderVisibility='Collapsed'; ArrowVisibility='Collapsed'; TicksVisibility='Visible' }
$themes['Prism-Light'] = T ($prismShape + @{ Paper='ECEDEF'; Panel='F6F6F7'; PanelStroke='00FFFFFF'; Hairline='D6D8DC'; Ink='1C1C1F'; Ink2='55585F'; Ink3='9A9DA4'; Accent='D71921'; AccentHover='E8363D'; OnAccent='FFFFFF'; Link='C4141B'; Track='D6D8DC'; ChipHover='FBFBFC'; ChipFill='EFEFF1'; ChipStroke='00FFFFFF'; Primary='2A2A2E'; PrimaryHover='3C3C42'; OnPrimary='FFFFFF'; HeaderBlock='F6F6F7'; OnHeader='1C1C1F'
  Section1='2A2A2E'; Section2='2A2A2E'; Section3='2A2A2E'; Section4='2A2A2E'; Section5='2A2A2E'; SectionInk='FFFFFF'
  SliderZoom='D71921'; SliderBrightness='D71921'; SliderReport='D71921'; ReadoutHue1='D71921'; ReadoutHue2='1C1C1F'; ReadoutHue3='1C1C1F'
  PanelEffect='<DropShadowEffect x:Key="PanelEffect" BlurRadius="30" ShadowDepth="12" Direction="315" Opacity="0.5" Color="#B4B7BE" />'
  PanelEffectLight='<DropShadowEffect x:Key="PanelEffectLight" BlurRadius="26" ShadowDepth="10" Direction="135" Opacity="1" Color="#FFFFFF" />'
  ChipEffect='<DropShadowEffect x:Key="ChipEffect" BlurRadius="8" ShadowDepth="3" Direction="315" Opacity="0.4" Color="#B4B7BE" />' })
$themes['Prism-Dark'] = T ($prismShape + @{ Paper='1A1A1C'; Panel='242427'; PanelStroke='00FFFFFF'; Hairline='38383D'; Ink='F2F2F3'; Ink2='B0B2B8'; Ink3='77797F'; Accent='FF4750'; AccentHover='FF6A71'; OnAccent='1A1A1C'; Link='FF5961'; Track='38383D'; ChipHover='34343A'; ChipFill='2E2E32'; ChipStroke='00FFFFFF'; Primary='E6E6E8'; PrimaryHover='FFFFFF'; OnPrimary='1A1A1C'; HeaderBlock='2E2E32'; OnHeader='F2F2F3'
  Section1='E6E6E8'; Section2='E6E6E8'; Section3='E6E6E8'; Section4='E6E6E8'; Section5='E6E6E8'; SectionInk='1A1A1C'
  SliderZoom='FF4750'; SliderBrightness='FF4750'; SliderReport='FF4750'; ReadoutHue1='FF5961'; ReadoutHue2='F2F2F3'; ReadoutHue3='F2F2F3'
  PanelEffect='<DropShadowEffect x:Key="PanelEffect" BlurRadius="30" ShadowDepth="12" Direction="315" Opacity="0.6" Color="#000000" />'
  PanelEffectLight='<DropShadowEffect x:Key="PanelEffectLight" BlurRadius="26" ShadowDepth="10" Direction="135" Opacity="0.5" Color="#3C3C42" />'
  ChipEffect='<DropShadowEffect x:Key="ChipEffect" BlurRadius="8" ShadowDepth="3" Direction="315" Opacity="0.55" Color="#000000" />' })
# CONTRAST (accessibility, roadmap "Polish" 10): black on white / white on black with one strong accent, thick borders,
# no shadows or tints, every text pair at WCAG AAA (7:1) or better. Studio's fonts and layout.
$contrastShape = @{ PanelCorner='4'; PanelBorder='3'; ChipCorner='4'; ChipBorder='2'; ButtonCorner='4'; AccentBarVisibility='Collapsed'; ViewfinderVisibility='Collapsed' }
$themes['Contrast-Light'] = T ($contrastShape + @{ Paper='FFFFFF'; Panel='FFFFFF'; PanelStroke='000000'; Hairline='000000'; Ink='000000'; Ink2='262626'; Ink3='595959'; Accent='0000CC'; AccentHover='2A2AE0'; OnAccent='FFFFFF'; Link='0000CC'; Track='8C8C8C'; ChipHover='E6E6E6'; ChipFill='FFFFFF'; ChipStroke='000000'; Primary='000000'; PrimaryHover='0000CC'; OnPrimary='FFFFFF'; HeaderBlock='000000'; OnHeader='FFFFFF'; Section1='000000'; Section2='000000'; Section3='000000'; Section4='000000'; Section5='000000'; SectionInk='FFFFFF' })
$themes['Contrast-Dark'] = T ($contrastShape + @{ Paper='000000'; Panel='000000'; PanelStroke='FFFFFF'; Hairline='FFFFFF'; Ink='FFFFFF'; Ink2='E6E6E6'; Ink3='BDBDBD'; Accent='FFD400'; AccentHover='FFE24D'; OnAccent='000000'; Link='FFD400'; Track='808080'; ChipHover='262626'; ChipFill='000000'; ChipStroke='FFFFFF'; Primary='FFD400'; PrimaryHover='FFFFFF'; OnPrimary='000000'; HeaderBlock='FFD400'; OnHeader='000000'; Section1='FFD400'; Section2='FFD400'; Section3='FFD400'; Section4='FFD400'; Section5='FFD400'; SectionInk='000000' })
$brushKeys = 'Panel','PanelStroke','Hairline','Ink','Ink2','Ink3','Accent','AccentHover','OnAccent','Link','Track','ChipHover','ChipFill','ChipStroke','Primary','PrimaryHover','OnPrimary','HeaderBlock','OnHeader','Good','Bad','Wait','Idle','OnStatus','Section1','Section2','Section3','Section4','Section5','SectionInk','Panel1','Panel2','Panel3','Panel4','Panel5','RowLens','RowResolution','RowFps','RowQuality','RowFocus','RowWb','RowFlicker','RowStab','RowTheme','RowAppearance','SliderZoom','SliderBrightness','SliderReport','ReadoutHue1','ReadoutHue2','ReadoutHue3'
foreach ($name in $themes.Keys) {
  $t = $themes[$name]; $L = New-Object System.Collections.Generic.List[string]
  # themes that do not set a per-row colour keep what they always had: panel colour for chips, accent for sliders, ink for numbers
  $defaults = @{ Panel1='Panel'; Panel2='Panel'; Panel3='Panel'; Panel4='Panel'; Panel5='Panel'; RowLens='Section2'; RowResolution='Section2'; RowFps='Section2'; RowQuality='Section2'; RowFocus='Section4'; RowWb='Section4'; RowFlicker='Section4'; RowStab='Section4'; RowTheme='Section5'; RowAppearance='Section5'; SliderZoom='Accent'; SliderBrightness='Accent'; SliderReport='Accent'; ReadoutHue1='Ink'; ReadoutHue2='Ink'; ReadoutHue3='Ink' }
  foreach ($dk in $defaults.Keys) { if (-not $t.ContainsKey($dk)) { $t[$dk] = $t[$defaults[$dk]] } }
  $L.Add('<!-- Generated from one table (tools/theme_selftest.ps1 checks every theme defines the same keys). Theme: ' + $name + '. See docs/DESIGN_PATTERNS.md "Themes". -->')
  $L.Add('<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"')
  $L.Add('                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"')
  $L.Add('                    xmlns:sys="clr-namespace:System;assembly=System.Runtime">')
  if ($name -like 'Gamer*') {
    $L.Add('    <DrawingBrush x:Key="PaperBrush" TileMode="Tile" Viewport="0,0,22,22" ViewportUnits="Absolute" Viewbox="0,0,22,22" ViewboxUnits="Absolute">')
    $L.Add('        <DrawingBrush.Drawing><DrawingGroup>')
    $L.Add('            <GeometryDrawing Brush="#0A0A0B"><GeometryDrawing.Geometry><RectangleGeometry Rect="0,0,22,22" /></GeometryDrawing.Geometry></GeometryDrawing>')
    $L.Add('            <GeometryDrawing Brush="#2A2A2F"><GeometryDrawing.Geometry><EllipseGeometry Center="11,11" RadiusX="0.9" RadiusY="0.9" /></GeometryDrawing.Geometry></GeometryDrawing>')
    $L.Add('        </DrawingGroup></DrawingBrush.Drawing>')
    $L.Add('    </DrawingBrush>')
  } else { $L.Add('    <SolidColorBrush x:Key="PaperBrush" Color="#' + $t.Paper + '" />') }
  foreach ($k in $brushKeys) { if ($k -eq 'HeaderBlock' -and $t.HeaderBlockXaml) { $L.Add('    ' + $t.HeaderBlockXaml); continue }; $L.Add('    <SolidColorBrush x:Key="' + $k + 'Brush" Color="#' + $t[$k] + '" />') }
  foreach ($k in 'PanelCorner','ChipCorner','ButtonCorner') { $L.Add('    <CornerRadius x:Key="' + $k + '">' + $t[$k] + '</CornerRadius>') }
  foreach ($k in 'PanelBorder','ChipBorder','HeaderIndent') { $L.Add('    <Thickness x:Key="' + $k + '">' + $t[$k] + '</Thickness>') }
  foreach ($ek in 'PanelEffect','PanelEffectLight','ChipEffect') { if ($t[$ek]) { $L.Add('    ' + $t[$ek]) } else { $L.Add('    <x:Null x:Key="' + $ek + '" />') } }
  foreach ($k in 'LabelFont','TitleFont','ChipFont','ReadoutFont','WordmarkFont') { $L.Add('    <FontFamily x:Key="' + $k + '">' + $t[$k] + '</FontFamily>') }
  foreach ($k in 'LabelSize','TitleSize','ReadoutSize','ReadoutLine','WordmarkSize') { $L.Add('    <sys:Double x:Key="' + $k + '">' + $t[$k] + '</sys:Double>') }
  foreach ($k in 'TitleWeight','ReadoutWeight') { $L.Add('    <FontWeight x:Key="' + $k + '">' + $t[$k] + '</FontWeight>') }
  $L.Add('    <sys:Boolean x:Key="UpperCase">' + $t.UpperCase + '</sys:Boolean>')
  foreach ($k in 'AccentBarVisibility','IconVisibility','BracketVisibility','ViewfinderVisibility','StarVisibility','ArrowVisibility','TicksVisibility') { $L.Add('    <Visibility x:Key="' + $k + '">' + $t[$k] + '</Visibility>') }
  $L.Add('    <sys:String x:Key="ArrowGlyph">&#x' + $t.ArrowGlyph + ';</sys:String>')
  1..5 | ForEach-Object { $L.Add('    <sys:String x:Key="Icon' + $_ + '">&#x' + $t["Icon$_"] + ';</sys:String>') }
  $L.Add('</ResourceDictionary>')
  [IO.File]::WriteAllText("$out\$name.xaml", ($L -join "`r`n") + "`r`n", (New-Object Text.UTF8Encoding($false)))
}
Get-ChildItem $out -Filter *.xaml | Select-Object Name, Length
