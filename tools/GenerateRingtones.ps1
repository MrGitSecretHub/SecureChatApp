# Synthesizes original embedded notification ringtones (WAV PCM 16-bit, 44100 Hz, mono)
# into the Android raw resources folder. Windows uses the same files as embedded
# resources, so the audio lives in a single physical place in the repository.
#
# Usage: powershell -File tools\GenerateRingtones.ps1
# The script contains no copyrighted material: every tone is generated mathematically.

param(
    [string]$Root = (Join-Path $PSScriptRoot "..\src\SecureChat.App\Platforms\Android\Resources\raw")
)

$ErrorActionPreference = "Stop"

New-Item -ItemType Directory -Force -Path $Root | Out-Null

$SampleRate = 44100

function Write-Wav {
    param(
        [string]$Path,
        [int]$SampleRate,
        [double[]]$Samples
    )

    # Normalize peak to ~0.80 to keep the tone clear but not loud.
    $peak = 0.0
    foreach ($s in $Samples) {
        $abs = [math]::Abs($s)
        if ($abs -gt $peak) { $peak = $abs }
    }
    if ($peak -le 0) { $peak = 1.0 }
    $scale = 0.80 / $peak

    $count = $Samples.Length
    $bytes = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($bytes)
    for ($i = 0; $i -lt $count; $i++) {
        $v = [int][math]::Round($Samples[$i] * $scale * 32767.0)
        if ($v -gt 32767) { $v = 32767 }
        if ($v -lt -32768) { $v = -32768 }
        $writer.Write([int16]$v)
    }
    $writer.Flush()
    $pcm = $bytes.ToArray()
    $writer.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $ascii = [System.Text.Encoding]::ASCII

    $bw.Write($ascii.GetBytes("RIFF"))
    $bw.Write([int](36 + $pcm.Length))
    $bw.Write($ascii.GetBytes("WAVE"))
    $bw.Write($ascii.GetBytes("fmt "))
    $bw.Write([int]16)
    $bw.Write([int16]1)             # PCM
    $bw.Write([int16]1)             # mono
    $bw.Write([int]$SampleRate)
    $bw.Write([int]($SampleRate * 2))
    $bw.Write([int16]2)             # block align
    $bw.Write([int16]16)            # bits per sample
    $bw.Write($ascii.GetBytes("data"))
    $bw.Write([int]$pcm.Length)
    $bw.Write($pcm)
    $bw.Flush()

    [System.IO.File]::WriteAllBytes($Path, $ms.ToArray())
    $bw.Dispose()
    $ms.Dispose()
}

function Invoke-Tone {
    param(
        [double]$Duration,
        [object[]]$Notes
    )

    # Notes: @(@{Freq=; Start=; Dur=; Amp=; Harmonic2=; Attack=; Decay=}, ...)
    $count = [int][math]::Ceiling($Duration * $SampleRate)
    $samples = New-Object "double[]" $count

    foreach ($note in $Notes) {
        $freq     = [double]$note.Freq
        $start    = [double]$note.Start
        $dur      = [double]$note.Dur
        $amp      = [double]$note.Amp
        $harm2    = [double]$note.Harmonic2
        $attack   = [double]$note.Attack
        $decay    = [double]$note.Decay

        $noteLen = [int][math]::Ceiling($dur * $SampleRate)
        for ($i = 0; $i -lt $noteLen; $i++) {
            $index = $start * $SampleRate + $i
            if ($index -ge $count) { break }
            $t = $i / $SampleRate

            $env = [math]::Exp(-$decay * $t)
            if ($attack -gt 0.0 -and $t -lt $attack) {
                $env *= ($t / $attack)
            }

            $value = [math]::Sin(2 * [math]::PI * $freq * $t)
            $value += $harm2 * [math]::Sin(2 * [math]::PI * $freq * 2 * $t)

            $samples[$index] += $amp * $env * $value
        }
    }

    return $samples
}

# ------------------------------------------------------------------
# 1. "Chime" - two-note ding, ~0.9 s.
# ------------------------------------------------------------------
$chime = Invoke-Tone -Duration 0.90 -Notes @(
    @{ Freq = 784.0;    Start = 0.000; Dur = 0.22; Amp = 0.62; Harmonic2 = 0.35; Attack = 0.004; Decay = 9.0 }
    @{ Freq = 1174.66;  Start = 0.075; Dur = 0.78; Amp = 1.00; Harmonic2 = 0.45; Attack = 0.004; Decay = 5.5 }
)
Write-Wav -Path (Join-Path $Root "ringtone_chime.wav") -SampleRate $SampleRate -Samples $chime

# ------------------------------------------------------------------
# 2. "Soft" - gentle low tone, slow attack, ~0.8 s.
# ------------------------------------------------------------------
$soft = Invoke-Tone -Duration 0.80 -Notes @(
    @{ Freq = 523.25; Start = 0.000; Dur = 0.60; Amp = 0.70; Harmonic2 = 0.18; Attack = 0.050; Decay = 5.0 }
    @{ Freq = 1046.5; Start = 0.000; Dur = 0.40; Amp = 0.18; Harmonic2 = 0.00; Attack = 0.080; Decay = 7.0 }
)
Write-Wav -Path (Join-Path $Root "ringtone_soft.wav") -SampleRate $SampleRate -Samples $soft

# ------------------------------------------------------------------
# 3. "Beep" - classic double beep, ~0.6 s.
# ------------------------------------------------------------------
$beep = Invoke-Tone -Duration 0.58 -Notes @(
    @{ Freq = 1000.0; Start = 0.000; Dur = 0.16; Amp = 1.00; Harmonic2 = 0.00; Attack = 0.004; Decay = 3.0 }
    @{ Freq = 1000.0; Start = 0.300; Dur = 0.16; Amp = 1.00; Harmonic2 = 0.00; Attack = 0.004; Decay = 3.0 }
)
Write-Wav -Path (Join-Path $Root "ringtone_beep.wav") -SampleRate $SampleRate -Samples $beep

# ------------------------------------------------------------------
# 4. "Ping" - bright short high tone, ~0.4 s.
# ------------------------------------------------------------------
$ping = Invoke-Tone -Duration 0.40 -Notes @(
    @{ Freq = 1568.0; Start = 0.000; Dur = 0.34; Amp = 1.00; Harmonic2 = 0.40; Attack = 0.003; Decay = 14.0 }
)
Write-Wav -Path (Join-Path $Root "ringtone_ping.wav") -SampleRate $SampleRate -Samples $ping

Get-ChildItem $Root -Filter "ringtone_*.wav" | ForEach-Object {
    $bytes = [System.IO.File]::ReadAllBytes($_.FullName)
    $riff = [System.Text.Encoding]::ASCII.GetString($bytes, 0, 4)
    $wave = [System.Text.Encoding]::ASCII.GetString($bytes, 8, 4)
    "{0}  {1} bytes  RIFF={2} WAVE={3}" -f $_.Name, $bytes.Length, $riff, $wave
}
