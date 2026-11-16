<#
.SYNOPSIS
  Engine smoke test: runs a dedicated server plus headless clients through a few scripted
  scenarios, then checks their logs. Exits 0 if every check passes, 1 otherwise.

.DESCRIPTION
  Scenarios (each with its own port and throwaway database, so a dev-run session is left alone):
    coop    - two players: hub, create/join a dungeon, wall check, take the pass, open the exit, both escape,
              run recorded, empty dungeon closed.
    combat  - three players with sword, ice wand and pistol fight enemies placed at the start;
              the wand user's aim ghost must show up for a teammate.
    death   - two unarmed players next to enemies: enemies must land hits (up close), the normal character
              returns to the hub, the hardcore one is deleted.
    city    - two players go through the City Gate and end up in the same generated city; its doorways, fences
              and walls match the layout; its 40 residents and its clock reach the clients; both return to the hub,
              and the empty city is closed.
    crowd   - a player runs at walking city residents: it is stopped at arm's length, never inside them.
    doors   - a player searches a body, takes what it carried, and uses its key on the door it fits (lock, unlock,
              open, close) and fails on a door without its key, then trades the keys in at the hub's locksmith;
              another player shoots a resident dead.
    drag    - a player drags a knocked-out resident away and lets go; a third player yanks the body away mid-drag;
              they come round later; another player knocks residents out with a bat.
    supply  - a player takes items from the hub's debug supply (dev servers).
    gather  - one player chops the hub's trees with an axe and puts the logs into the hub storage; another
              starts a dungeon, which must get its trees (and still bake navigation around them).
    breach  - in an empty city, a player walks (validated movement) to three locked doors: kicks one in, picks
              one, and shoots one's deadbolt out with a pump shotgun; the noise is heard; the shot-out lock won't lock.
    floors  - a player kicks into a building with stairs and climbs through its rooms to the top floor.
    stealth - with residents: a player trespasses in an occupied building, another grabs a passer-by, a third fires
              into the air. Residents speak up, the police are called and respond, and someone grabs or fights a player.
  Every scenario also checks: no engine/script errors, remote players never fly off vertically,
  every process quits on its own, and the server saves and closes the database.

  Logs are kept in -OutDir\<scenario>\ for reading after a failure.

.EXAMPLE
  tools\smoke-test.cmd
  tools\smoke-test.cmd -Only combat -NoBuild
#>
param(
    [string[]]$Only = @(),
    [switch]$NoBuild,
    [string]$OutDir = 'E:\claude\_tmp\smoke'
)

$ErrorActionPreference = 'Stop'
$Project = Split-Path -Parent $PSScriptRoot
$GodotConsole = 'E:\Godot Install\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'

# Keep everything off C: and make sure Godot finds the E: .NET runtime (see E:\claude\CLAUDE.md).
$env:DOTNET_ROOT = 'E:\claude\tools\dotnet'
$env:PATH = 'E:\claude\tools\dotnet;' + $env:PATH
$env:TEMP = 'E:\claude\_tmp'
$env:TMP = 'E:\claude\_tmp'
$env:APPDATA = 'E:\claude\_appdata'

# Lines matching these are engine noise, not failures.
$AllowedErrors = @()

# ---------------------------------------------------------------- scenarios
# Times are seconds. Clients start once the server is listening; the server quits last.
# A client's args: --connect/--user/--password are added automatically.

$Scenarios = [ordered]@{
    city = @{
        Port   = 7945
        Server = @('--dev-skip-proximity', '--dungeon-empty-timeout', '3', '--city-seed', '11', '--dev-quit-after', '20')
        Clients = [ordered]@{
            Kermit    = @('--color', '#3cb043', '--dev-city', '--dev-check-city', '--dev-return-after', '6', '--dev-log', '--dev-quit-after', '12')
            Blueberry = @('--color', '#4f86f7', '--dev-city', '--dev-return-after', '7', '--dev-log', '--dev-quit-after', '13')
        }
        Checks = {
            Expect server 'city 2: 5x5, seed 11, loops 15%, \d+ parcels' 'a 5x5 city is generated from the seed'
            ExpectNot server 'city 3:' 'only one city is generated for the two players'
            Expect server "\('Kermit'\) entered instance 2" 'Kermit enters the city'
            Expect server "\('Blueberry'\) entered instance 2" 'Blueberry joins the same city'
            Expect Kermit 'city check: (\d+)/\1 doors, fences and walls match' 'built doorways, fences and walls match the layout'
            Expect server 'city 2: 40 people \(\d+ civilians, \d+ workers, 6 police, \d+ shopkeepers(, \d+ security guards)?\), clock 08:00' 'the city gets 40 residents and a clock'
            Expect Kermit 'city: 40 residents in view \(.*\), \d+ walking, clock 08:\d\d' "Kermit sees all the residents, and the city's clock"
            Expect Kermit "remote player \d+ \('Blueberry', #4f86f7\) spawned" 'Kermit sees Blueberry in the city' -After 'entered instance 2 \(city\)'
            Expect Kermit 'entered instance 1 \(hub\)' 'Kermit returns to the hub' -After 'entered instance 2 \(city\)'
            Expect Blueberry 'entered instance 1 \(hub\)' 'Blueberry returns to the hub' -After 'entered instance 2 \(city\)'
            Expect server 'closed empty city 2' 'the empty city is closed'
        }
    }
    crowd = @{
        Port   = 7946
        Server = @('--dev-skip-proximity', '--dev-starter-kit', '--city-seed', '4', '--city-start-hour', '9', '--dev-quit-after', '50')
        Clients = [ordered]@{
            Kermit    = @('--dev-city', '--dev-chase-npc', '--dev-quit-after', '40')
            Tangerine = @('--dev-city', '--dev-weapon', 'Ice Wand', '--dev-aim', '--dev-fight', '--dev-quit-after', '30')
        }
        Checks = {
            Expect server ' is frozen for 5 s' "Tangerine's ice wand freezes residents, like dungeon enemies"
            Expect Kermit 'closest resident so far: 0\.[67]\d m' 'Kermit runs at a resident and is stopped at arm''s length'
            ExpectNot Kermit 'closest resident so far: 0\.[0-5]\d m' 'Kermit never walks into a resident'
        }
    }
    doors = @{
        Port   = 7947
        Server = @('--dev-skip-proximity', '--dev-starter-kit', '--city-seed', '4', '--city-start-hour', '9', '--dev-kill-npcs', '2', '--dev-oblivious',
                   '--dev-quit-after', '50')
        Clients = [ordered]@{
            Kermit    = @('--dev-city', '--dev-doors', '--dev-check-city', '--dev-return-after', '18', '--dev-trade-keys',
                          '--dev-quit-after', '25')
            Blueberry = @('--dev-city', '--dev-weapon', 'Pistol', '--dev-fight', '--dev-quit-after', '40')
        }
        Checks = {
            Expect Kermit 'city check: (\d+)/\1 doors, fences and walls match' 'every closed door blocks its doorway; walls and fences hold'
            Expect server "'Kermit' searches the body of" 'Kermit searches a body'
            Expect server "'Kermit' took Key from the body of" 'and takes the key it carried'
            Expect server "'Kermit' took Coin x\d+ from the body of" 'and the rest (stacks too)'
            Expect server "door \d+ locked by 'Kermit' \(with a key\)" 'the key locks its door'
            Expect server "door \d+ unlocked by 'Kermit' \(with a key\)" 'and unlocks it'
            Expect server "door \d+ opened by 'Kermit'" 'Kermit opens the door'
            Expect server "door \d+ closed by 'Kermit'" 'and closes it'
            Expect Kermit "notice: You don't have the key to this door.|Picking the lock" "without its key, a door won't lock or unlock (with lockpicks, picking starts instead)"
            Expect server "'Blueberry' hit .* with Pistol .*killed in city" 'Blueberry shoots a resident dead'
            Expect server "'Kermit' traded \d+ key\(s\) for \d+ coins at the locksmith" 'back in the hub, the locksmith buys the keys'
        }
    }
    drag = @{
        Port   = 7948
        Server = @('--dev-skip-proximity', '--dev-starter-kit', '--city-seed', '4', '--city-start-hour', '9', '--dev-knockout-npcs', '1', '--dev-oblivious',
                   '--city-wake-seconds', '15', '--dev-quit-after', '50')
        Clients = [ordered]@{
            Kermit    = @('--dev-city', '--dev-drag', '--dev-quit-after', '25')
            Blueberry = @('--dev-city', '--dev-weapon', 'Bat', '--dev-fight', '--dev-chase-npc', '--dev-quit-after', '40')
            Grape     = @('--dev-city', '--dev-drag', '--dev-drag-delay', '4', '--dev-quit-after', '27') # quits apart from Kermit (simultaneous disconnects race in ENet)
        }
        Checks = {
            Expect server "'Kermit' starts dragging .* \(unconscious\)" 'Kermit takes hold of a knocked-out resident'
            Expect server "'Grape' yanks .* away from 'Kermit'" 'Grape yanks the body away from Kermit'
            Expect Kermit 'notice: Grape yanks .* away from you!' 'Kermit is told'
            Expect Kermit 'dragging ' "Kermit's client knows (slow walk)"
            Expect server "'Kermit' lets go of .*, ([3-9]|\d{2,})\.\d m from where" 'and drags them a good way before letting go'
            Expect server ' comes round' 'the knocked-out resident comes round later'
            Expect server "'Blueberry' knocked out .* with Bat" 'a bat knocks a resident out'
        }
    }
    supply = @{
        Port   = 7949
        Server = @('--dev-skip-proximity', '--dev-supply', '--dev-quit-after', '15')
        Clients = [ordered]@{
            Kermit = @('--dev-supply-take', 'Bat,Pistol Rounds,Concrete Block', '--dev-quit-after', '8')
        }
        Checks = {
            Expect Kermit 'debug supply: \d+ items' 'the debug supply opens'
            Expect server "'Kermit' took Bat from the debug supply" 'a weapon from the supply'
            Expect server "'Kermit' took Pistol Rounds x30 from the debug supply" 'a stack from the supply'
            Expect Kermit 'inventory .*Bat.*Pistol Rounds x30.*Concrete Block' 'all of it lands in the inventory'
        }
    }
    coop = @{
        Port   = 7941
        Server = @('--dev-skip-proximity', '--dungeon-empty-timeout', '3', '--dev-quit-after', '24')
        Clients = [ordered]@{
            Kermit    = @('--color', '#3cb043', '--dev-dungeon', 'new:5x5x400', '--dev-check-walls', '--dev-solve',
                          '--dev-log', '--dev-quit-after', '17')
            Blueberry = @('--color', '#4f86f7', '--dev-dungeon', 'join:Kermit', '--dev-escape-after', '6',
                          '--dev-autowalk', '--dev-log', '--dev-quit-after', '18')
        }
        Checks = {
            Expect server "registered account 'Kermit'" 'accounts are registered'
            Expect server "\('Kermit'\) entered instance 2" 'Kermit enters the new dungeon'
            Expect server "\('Blueberry'\) entered instance 2" 'Blueberry joins it'
            Expect server 'dungeon 2: navigation baked' 'navigation is baked'
            Expect Kermit 'wall check: (\d+)/\1 sides match' 'built walls match the maze layout'
            Expect Kermit "remote player \d+ \('Blueberry', #4f86f7\) spawned" 'Kermit sees Blueberry (in her color) in the dungeon' -After 'entered instance 2 \(dungeon\)'
            Expect Blueberry "remote player \d+ \('Kermit', #3cb043\) spawned" 'Blueberry sees Kermit in the dungeon' -After 'entered instance 2 \(dungeon\)'
            Expect server "'Kermit' picked up Dungeon Pass" 'the Dungeon Pass is picked up'
            Expect server "'Kermit' opened the exit of instance 2" 'the exit is opened'
            Expect server "'Kermit' escaped instance 2; 1 still inside" 'Kermit escapes first'
            Expect server "'Blueberry' escaped instance 2; 0 still inside" 'Blueberry escapes too'
            Expect Blueberry 'entered instance 1 \(hub\)' 'Blueberry is back in the hub' -After 'notice: You escaped'
            Expect server 'run \d+ \(instance 2\) completed: 2/2 escaped' 'the run is recorded as 2/2 escaped'
            Expect server 'closed empty dungeon 2' 'the empty dungeon is closed'
        }
    }
    combat = @{
        Port   = 7942
        Server = @('--dev-skip-proximity', '--dungeon-empty-timeout', '3', '--dev-starter-kit', '--dev-enemies-near-start',
                   '--dev-quit-after', '30')
        # Kermit and Blueberry fight together; Tangerine's pistol one-shots enemies, so she gets her own
        # dungeon (otherwise the sword never reaches anything). That also checks two dungeons stay separate.
        Clients = [ordered]@{
            Kermit    = @('--dev-dungeon', 'new:3x3x40', '--dev-weapon', 'Sword', '--dev-fight', '--dev-log', '--dev-quit-after', '22')
            Blueberry = @('--color', '#4f86f7', '--dev-dungeon', 'join:Kermit', '--dev-weapon', 'Ice Wand', '--dev-aim', '--dev-fight',
                          '--dev-log', '--dev-quit-after', '23')
            Tangerine = @('--dev-dungeon', 'new:3x3x40', '--dev-weapon', 'Pistol', '--dev-fight', '--dev-quit-after', '24')
        }
        Checks = {
            Expect server "'Kermit' weapon: Sword" 'Kermit equips the sword'
            Expect server "'Blueberry' weapon: Ice Wand" 'Blueberry equips the ice wand'
            Expect server "'Tangerine' weapon: Pistol" 'Tangerine equips the pistol'
            Expect server "'Kermit' hit Enemy\d+ with Sword" 'sword hits land'
            Expect server "('Kermit' hit|magic \(\w+\) hit) Enemy\d+ .*killed" 'Kermit and Blueberry kill enemies'
            Expect server "'Blueberry' cast Ice Wand" 'the ice wand casts'
            Expect server "'Tangerine' hit Enemy\d+ with Pistol .*killed" 'the pistol kills enemies' -Min 2
            Expect Kermit "showing Blueberry's aim ghost" "teammates see Blueberry's aim ghost"
            Expect Kermit "remote player \d+ \('Blueberry'" 'Kermit sees Blueberry in the dungeon' -After 'entered instance \d+ \(dungeon\)'
            ExpectNot Tangerine "remote player \d+ \('(Kermit|Blueberry)'.*spawned" "Tangerine's dungeon doesn't show the other dungeon's players" -After 'entered instance \d+ \(dungeon\)'
            ExpectNot Kermit "remote player \d+ \('Tangerine'.*spawned" "Kermit's dungeon doesn't show Tangerine" -After 'entered instance \d+ \(dungeon\)'
        }
    }
    # Each victim is alone in their own dungeon: enemies only notice players inside their 90 degree view
    # cone, so with two players one can stand unnoticed beside an enemy that faces the other way.
    death = @{
        Port   = 7943
        Server = @('--dev-skip-proximity', '--dungeon-empty-timeout', '3', '--dev-enemies-near-start', '--dev-quit-after', '33')
        Clients = [ordered]@{
            Grape     = @('--hardcore', '--dev-dungeon', 'new:3x3x40', '--dev-quit-after', '26')
            Tangerine = @('--dev-dungeon', 'new:3x3x40', '--dev-quit-after', '27')
        }
        Checks = {
            Expect server "created hardcore character 'Grape'" 'Grape is created as hardcore'
            Expect server "Enemy\d+ hit 'Grape'" 'enemies hit Grape up close'
            Expect server "Enemy\d+ hit 'Tangerine'" 'enemies hit Tangerine up close'
            Expect Tangerine 'took \d+ damage' "Tangerine's client sees the damage"
            Expect server "'Tangerine' died in instance \d+; returning to the hub" 'Tangerine dies'
            Expect Tangerine 'entered instance 1 \(hub\)' 'Tangerine wakes up in the hub' -After 'notice: You died'
            Expect server "hardcore character 'Grape' died in instance \d+; deleting it" 'Grape dies and is deleted'
            Expect Grape 'Grape died and was a hardcore character' "Grape's client is told"
            ExpectNot Grape 'entered instance' "Grape's client stays out of the world afterwards" -After 'hardcore character'
            Expect server 'run \d+ \(instance \d+\) abandoned: 0/\d escaped' 'both runs are recorded as abandoned' -Min 2
        }
    }
    gather = @{
        Port   = 7944
        Server = @('--dev-skip-proximity', '--dungeon-empty-timeout', '3', '--dev-starter-kit', '--dev-quit-after', '30')
        Clients = [ordered]@{
            Kermit    = @('--dev-weapon', 'Axe', '--dev-gather', '3', '--dev-quit-after', '22')
            Blueberry = @('--dev-dungeon', 'new:5x5x400', '--dev-quit-after', '12')
        }
        Checks = {
            Expect server "'Kermit' weapon: Axe" 'Kermit equips the axe'
            Expect server "'Kermit' gathered Log from Resource\d+ in instance 1 \(now 3\)" 'Kermit chops 3 logs from the hub trees'
            Expect Kermit 'notice: You get Log' "Kermit's client is told about each log" -Min 3
            Expect server "'Kermit' opened their storage \(0 slot\(s\) used\)" 'Kermit opens an empty storage'
            Expect Kermit 'storage \(1 slots used\): Log x3' 'the logs end up in the storage'
            Expect server 'dungeon 2: navigation baked in \d+ ms, 5 enemies, 5 trees' 'a new dungeon gets 5 trees'
        }
    }
    breach = @{
        Port   = 7950
        # Nobody lives here (--city-people 0), so every door is locked and nobody interferes.
        Server = @('--dev-skip-proximity', '--dev-starter-kit', '--city-seed', '4', '--city-start-hour', '12', '--city-people', '0', '--dev-quit-after', '110')
        Clients = [ordered]@{
            Kermit = @('--dev-city', '--dev-breach', '--dev-quit-after', '100')
        }
        Checks = {
            Expect server "'Kermit' kicks door \d+ \(1/\d\)" 'a kick that the door holds against'
            Expect server "'Kermit' kicks in door \d+" 'Kermit kicks a locked door in'
            Expect Kermit 'dev-breach: door \d+ kicked in' 'the door is broken (open for good) on the client too'
            Expect server 'noise in city 2: door kicked' 'kicking is loud'
            Expect server "'Kermit' starts picking door \d+" 'Kermit starts picking a lock'
            Expect Kermit 'dev-breach: door \d+ picked' 'and gets it open (a failed try breaks a pick)'
            Expect server "'Kermit' shoots out the lock of door \d+" 'a slug through the deadbolt destroys the lock'
            Expect Kermit 'dev-breach: door \d+ lock shot out' 'the client sees the lock gone'
            Expect server 'noise in city 2: gunshot' 'the shot is heard'
            Expect Kermit "notice: The lock's been shot out." "the door can't be locked again"
            ExpectNot server 'rejected a move by peer' 'walking there is never rejected by movement validation'
        }
    }
    floors = @{
        Port   = 7952
        # A player walks into the nearest building with stairs (kicking the door in) and climbs to its top floor.
        Server = @('--dev-skip-proximity', '--city-seed', '4', '--city-start-hour', '12', '--city-people', '0', '--dev-quit-after', '90')
        Clients = [ordered]@{
            Kermit = @('--dev-city', '--dev-upstairs', '--dev-quit-after', '80')
        }
        Checks = {
            Expect Kermit 'dev-upstairs: \w+ \d+, [2-9] floors, \d+ rooms' 'the building has upper floors and rooms'
            Expect Kermit 'dev-upstairs: reached floor [1-9] \(height (3\.(19|20)|6\.(39|40)|9\.(59|60))\)' 'Kermit walks up the stairs to the top floor, standing on it'
            ExpectNot server 'rejected a move by peer' 'climbing the stairs is never rejected by movement validation'
        }
    }
    stealth = @{
        Port   = 7951
        # 10:00: workers at work, civilians out. Kermit walks into an occupied office or warehouse and stays (and
        # goes quietly if arrested); Grape grabs a passer-by and holds on; Blueberry fires into the air (and is arrested for it).
        Server = @('--dev-skip-proximity', '--dev-starter-kit', '--city-seed', '4', '--city-start-hour', '10', '--dev-quit-after', '100')
        Clients = [ordered]@{
            Kermit    = @('--dev-city', '--dev-trespass', '--dev-quit-after', '80')
            Blueberry = @('--dev-city', '--dev-weapon', 'Pistol', '--dev-shoot-air', '--dev-shoot-delay', '4', '--dev-quit-after', '85')
            Grape     = @('--dev-city', '--dev-grab', '--dev-hit-hostage', '--dev-strangle', '--dev-quit-after', '90')
        }
        Checks = {
            Expect Kermit 'dev-trespass: walking into ' 'Kermit heads into an occupied building'
            Expect Kermit ' says: ' 'residents speak up (warnings, shouts)'
            Expect Kermit 'alert: (Suspicious|Wanted)' "Kermit's HUD shows he's been noticed"
            Expect server "reports .*'(Kermit|Blueberry|Grape)'" 'someone calls the police on a player'
            Expect server 'responding' 'police respond to a report'
            Expect server "noise in city 2: gunshot" "Blueberry's gunfire is heard across the city"
            Expect server "'Grape' grabs .*\((Civilian|Worker)" 'Grape grabs a resident'
            Expect server "'Grape' strikes the hostage .* with fists for 5" 'and strikes them (they give in)'
            Expect server "('Grape' strangles .* unconscious|breaks free from 'Grape')" 'Grape strangles them unconscious, or they wriggle free'
            Expect server "(fires .* at|hits|grabs) '(Kermit|Blueberry|Grape)'" 'residents fight back or grab a player'
            Expect server "arrests '(Kermit|Blueberry|Grape)'" 'the police arrest someone'
        }
    }
}

# ---------------------------------------------------------------- helpers

function Quote([string]$arg) {
    if ($arg -match '[\s"]') { return '"' + ($arg -replace '"', '\"') + '"' }
    return $arg
}

function Start-Godot([string]$name, [string[]]$userArgs, [string]$dir) {
    # --smoke-test marks the process so tools\dev-stop.cmd (run by dev-run) leaves it alone.
    $all = @('--headless', '--path', $Project, '--') + $userArgs + @('--smoke-test')
    $argLine = ($all | ForEach-Object { Quote $_ }) -join ' '
    $p = Start-Process -FilePath $GodotConsole -ArgumentList $argLine -NoNewWindow -PassThru `
        -RedirectStandardOutput (Join-Path $dir "$name.log") -RedirectStandardError (Join-Path $dir "$name.err.log")
    return $p
}

function Read-Log([string]$dir, [string]$name) {
    $lines = @()
    foreach ($f in @("$name.log", "$name.err.log")) {
        $path = Join-Path $dir $f
        if (Test-Path $path) { $lines += @(Get-Content -LiteralPath $path -Encoding UTF8) }
    }
    return , $lines
}

$script:Results = New-Object System.Collections.Generic.List[object]

function Add-Result([string]$scenario, [bool]$ok, [string]$text, [string]$detail = '') {
    $script:Results.Add([pscustomobject]@{ Scenario = $scenario; Ok = $ok; Text = $text; Detail = $detail })
    $mark = if ($ok) { 'PASS' } else { 'FAIL' }
    $color = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("  [{0}] {1}" -f $mark, $text) -ForegroundColor $color
    if (-not $ok -and $detail) { Write-Host "         $detail" -ForegroundColor DarkGray }
}

function Get-Lines([string]$who, [string]$after) {
    $lines = $script:Logs[$who]
    if ($null -eq $lines) { throw "no log named '$who'" }
    if ($after) {
        $i = 0
        while ($i -lt $lines.Count -and $lines[$i] -notmatch $after) { $i++ }
        if ($i -ge $lines.Count) { return , @() }
        return , @($lines[($i + 1)..($lines.Count - 1)] | Where-Object { $_ -ne $null })
    }
    return , $lines
}

function Expect([string]$who, [string]$pattern, [string]$text, [string]$After = '', [int]$Min = 1) {
    $count = @((Get-Lines $who $After) | Where-Object { $_ -match $pattern }).Count
    $where = if ($After) { " after /$After/" } else { '' }
    Add-Result $script:Current ($count -ge $Min) $text "expected $Min+ line(s) matching /$pattern/ in $who.log$where, found $count"
}

function ExpectNot([string]$who, [string]$pattern, [string]$text, [string]$After = '') {
    $hits = @((Get-Lines $who $After) | Where-Object { $_ -match $pattern })
    $first = if ($hits.Count) { $hits[0].Trim() } else { '' }
    Add-Result $script:Current ($hits.Count -eq 0) $text "unexpected line in $who.log: $first"
}

# Checks that apply to every scenario.
function Check-Common([string[]]$names, [hashtable]$exits) {
    foreach ($n in $names) {
        Add-Result $script:Current ($exits[$n] -eq 'exited') "$n quits on its own" "process was $($exits[$n])"

        $errors = @($script:Logs[$n] | Where-Object {
                $_ -match '^(ERROR|SCRIPT ERROR|USER ERROR):|Unhandled exception|^\s*at .*Exception|^[\w.]+Exception:' })
        foreach ($allowed in $AllowedErrors) { $errors = @($errors | Where-Object { $_ -notmatch $allowed }) }
        $first = if ($errors.Count) { $errors[0].Trim() } else { '' }
        Add-Result $script:Current ($errors.Count -eq 0) "$n has no errors" "$($errors.Count) error line(s), first: $first"

        if ($n -ne 'server') {
            ExpectNot $n 'auth failed|Could not connect|Disconnected from the server' "$n stays connected"
            # The "launched into the sky" bug: remote players must stay near the floor.
            $ys = @($script:Logs[$n] | ForEach-Object {
                    if ($_ -match 'remote player \d+ at \(([-\d.e]+), ([-\d.e]+), ([-\d.e]+)\)') { [double]::Parse($Matches[2], [Globalization.CultureInfo]::InvariantCulture) } })
            if ($ys.Count) {
                $bad = @($ys | Where-Object { $_ -lt -1 -or $_ -gt 3 })
                $maxY = ($ys | Measure-Object -Maximum).Maximum
                Add-Result $script:Current ($bad.Count -eq 0) "$n sees remote players stay on the floor ($($ys.Count) samples)" "$($bad.Count) sample(s) outside -1..3 m, highest $maxY"
            }
        }
    }
    Expect server 'item audit: no duplicated or unknown item serials' 'the startup item audit is clean'
    ExpectNot server 'DUPLICATE ITEM' 'no duplicated items are detected while playing'
    Expect server 'saved and closed database' 'the server saves and closes the database'
}

# ---------------------------------------------------------------- run

if (-not (Test-Path $GodotConsole)) { throw "Godot not found: $GodotConsole" }

if (-not $NoBuild) {
    Write-Host 'Building...'
    & dotnet build (Join-Path $Project 'ExploreTogether.csproj') -nologo -v q
    if ($LASTEXITCODE -ne 0) { Write-Host 'SMOKE TEST FAILED: build failed' -ForegroundColor Red; exit 1 }
}

$names = @($Scenarios.Keys)
if ($Only.Count) {
    $unknown = @($Only | Where-Object { $names -notcontains $_ })
    if ($unknown.Count) { throw "unknown scenario(s): $($unknown -join ', '); known: $($names -join ', ')" }
    $names = @($names | Where-Object { $Only -contains $_ })
}

$started = Get-Date
foreach ($scenarioName in $names) {
    $s = $Scenarios[$scenarioName]
    $script:Current = $scenarioName
    $dir = Join-Path $OutDir $scenarioName
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
    New-Item -ItemType Directory -Force $dir | Out-Null
    Write-Host ''
    Write-Host "== $scenarioName" -ForegroundColor Cyan

    $procs = [ordered]@{}
    $serverArgs = @('--server', '--port', "$($s.Port)", '--db', (Join-Path $dir 'server.db')) + $s.Server
    $procs['server'] = Start-Godot 'server' $serverArgs $dir

    # Wait for the server to listen before starting clients.
    $deadline = (Get-Date).AddSeconds(20)
    $listening = $false
    while ((Get-Date) -lt $deadline -and -not $procs['server'].HasExited) {
        $log = Join-Path $dir 'server.log'
        if ((Test-Path $log) -and (Select-String -LiteralPath $log -Pattern 'listening on port' -Quiet)) { $listening = $true; break }
        Start-Sleep -Milliseconds 250
    }
    if ($listening) {
        foreach ($c in $s.Clients.Keys) {
            $clientArgs = @('--connect', "127.0.0.1:$($s.Port)", '--user', $c, '--password', 'devpass') + $s.Clients[$c]
            $procs[$c] = Start-Godot $c $clientArgs $dir
        }
    }

    # Every process quits by itself (--dev-quit-after); anything still running well after that is killed.
    $quitAfter = [double]($s.Server[[array]::IndexOf($s.Server, '--dev-quit-after') + 1])
    $deadline = (Get-Date).AddSeconds($quitAfter + 20)
    $exits = @{}
    foreach ($n in $procs.Keys) {
        $p = $procs[$n]
        $left = [int][Math]::Max(0, ($deadline - (Get-Date)).TotalMilliseconds)
        if ($p.WaitForExit($left)) { $exits[$n] = 'exited' }
        else {
            $exits[$n] = 'still running (killed)'
            try { $p.Kill() } catch {}
        }
    }

    $script:Logs = @{}
    foreach ($n in $procs.Keys) { $script:Logs[$n] = Read-Log $dir $n }

    if (-not $listening) {
        Add-Result $scenarioName $false 'the server starts listening' "see $dir\server.log"
        continue
    }
    & $s.Checks
    Check-Common @($procs.Keys) $exits
}

$failed = @($script:Results | Where-Object { -not $_.Ok })
$elapsed = [int]((Get-Date) - $started).TotalSeconds
Write-Host ''
if ($failed.Count) {
    Write-Host "SMOKE TEST FAILED: $($failed.Count) of $($script:Results.Count) checks failed (${elapsed}s). Logs: $OutDir" -ForegroundColor Red
    foreach ($f in $failed) { Write-Host "  [$($f.Scenario)] $($f.Text)" -ForegroundColor Red }
    exit 1
}
Write-Host "SMOKE TEST PASSED: $($script:Results.Count) checks in $($names.Count) scenario(s) (${elapsed}s). Logs: $OutDir" -ForegroundColor Green
exit 0
