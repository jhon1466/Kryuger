import { createContext, createMemo, createResource, createSignal, For, onCleanup, onMount, Show, useContext, type JSX } from 'solid-js';
import { invoke } from '@tauri-apps/api/core';
import { listen } from '@tauri-apps/api/event';
import {
  Activity, ArrowRight, Boxes, Check, Coins, Cpu, FolderOpen, Gamepad2, Gauge, Gift, HardDrive, LayoutDashboard,
  Play, Power, RefreshCw, Save, Search, Server, ShieldCheck, Trash2, SlidersHorizontal, Sparkles, Square, Users, Wifi, X,
} from 'lucide-solid';
import { initialLanguage, LANGUAGES, translate, type Language } from '../i18n';

type Status = {
  appVersion: string; repoRoot: string; managed: boolean; installed: boolean; built: boolean; localCommit: string;
  serverRunning: boolean; gameDir: string | null; hookInstalled: boolean; gmReady: boolean; dataDir: string;
};
type Player = { id: number; name: string; openId: string; level: number; experience: number; maxGun: number; maxEquip: number; maxTeam: number; gem: number; mp: number; ammo: number; mre: number; part: number; guns: number; equips: number; fairies: number };
type Entry = { id: number; name: string; rank?: number; gun?: number; type?: number };
type Catalog = { guns: Entry[]; items: Entry[]; equips: Entry[]; fairies: Entry[]; skins: Entry[] };
type Tab = 'server' | 'overview' | 'accounts' | 'operations';

const I18n = createContext<(key: string) => string>((k) => k);
const useT = () => useContext(I18n);

const gm = <T,>(method: string, path: string, body?: unknown) =>
  invoke<T>('gm_request', { method, path, body: body === undefined ? null : JSON.stringify(body) });

const routes: { id: Tab; label: string; title: string; icon: typeof Server }[] = [
  { id: 'server', label: 'navServer', title: 'titleServer', icon: Server },
  { id: 'overview', label: 'navOverview', title: 'titleOverview', icon: LayoutDashboard },
  { id: 'accounts', label: 'navAccounts', title: 'titleAccounts', icon: Users },
  { id: 'operations', label: 'navOperations', title: 'titleOperations', icon: Gift },
];

export default function KryugerApp() {
  const [language, setLanguage] = createSignal<Language>(initialLanguage());
  const t = (key: string) => translate(language(), key);
  const changeLanguage = (lang: Language) => {
    setLanguage(lang);
    try { localStorage.setItem('kryuger-gm-language', lang); } catch { /* storage unavailable */ }
    void invoke('manager_set_language', { lang });
  };
  onMount(() => void invoke('manager_set_language', { lang: language() }));

  const [tab, setTab] = createSignal<Tab>('server');
  const [status, setStatus] = createSignal<Status>();
  const [notice, setNotice] = createSignal<{ text: string; error?: boolean }>();
  const [lastAction, setLastAction] = createSignal('');
  const notify = (text: string, error = false) => { setNotice({ text, error }); setLastAction(text); setTimeout(() => setNotice(undefined), 5000); };
  const refresh = async () => { try { setStatus(await invoke<Status>('manager_status')); } catch { /* app closing */ } };
  onMount(() => { void refresh(); const timer = setInterval(refresh, 3000); onCleanup(() => clearInterval(timer)); });
  const online = () => !!status()?.gmReady;

  const [players, { refetch: reloadPlayers }] = createResource(online, (on) => (on ? gm<Player[]>('GET', '/gm/players') : Promise.resolve([] as Player[])));
  const [catalog] = createResource(online, (on) => (on ? gm<Catalog>('GET', '/gm/catalog') : Promise.resolve(undefined)));
  const route = () => routes.find((r) => r.id === tab())!;

  return (
    <I18n.Provider value={t}>
      <div class="app-shell flex h-screen min-h-[720px] w-screen min-w-[1080px] overflow-hidden bg-ink-950 text-ice-100">
        <aside class="sidebar relative flex w-[248px] shrink-0 flex-col border-r border-white/[0.07] bg-[#111416] px-4 py-5">
          <div class="brand flex items-center gap-3 px-2 pb-8">
            <div class="brand-mark grid size-12 place-items-center rounded-xl border border-white/10 bg-[#252a2e] p-1 shadow-[0_0_28px_#f2762b25]"><img src="/kryuger.png" alt="Kryuger" class="h-full w-full rounded-lg object-cover" /></div>
            <div><div class="text-[15px] font-black tracking-[.22em]">KRYUGER</div><div class="mt-0.5 text-[10px] font-semibold tracking-[.28em] text-aqua-300">GM CONTROL</div></div>
          </div>
          <div class="mb-2 flex items-center justify-between px-3 text-[10px] font-bold uppercase tracking-[.2em] text-ice-500">
            <span>{t('workspace')}</span>
            <select aria-label={t('language')} class="language-select" value={language()} onChange={(e) => changeLanguage(e.currentTarget.value as Language)}>
              <For each={LANGUAGES}>{(l) => <option value={l.id}>{l.label}</option>}</For>
            </select>
          </div>
          <nav class="flex flex-col gap-1">
            <For each={routes}>{(r) => { const Icon = r.icon; return (
              <button onClick={() => setTab(r.id)} class={`nav-item group flex h-11 items-center gap-3 rounded-lg px-3 text-left text-[13px] font-semibold transition ${tab() === r.id ? 'active-nav' : 'text-ice-300 hover:bg-white/[0.045] hover:text-white'}`}>
                <Icon size={17} stroke-width={1.8} /><span>{t(r.label)}</span><Show when={tab() === r.id}><ArrowRight class="ml-auto text-aqua-300" size={15} /></Show>
              </button>); }}</For>
          </nav>
          <div class="mt-8 mb-2 px-3 text-[10px] font-bold uppercase tracking-[.2em] text-ice-500">{t('server')}</div>
          <div class="mx-1 rounded-xl border border-white/[0.07] bg-white/[0.025] p-3">
            <div class="flex items-center gap-2 text-xs font-semibold"><span class={`status-dot ${status()?.serverRunning ? 'online' : ''}`}></span>{status()?.serverRunning ? t('kryugerLocal') : t('disconnected')}</div>
            <div class="mt-2 truncate pl-4 text-[11px] text-ice-500">127.0.0.1:5000</div>
          </div>
          <div class="mt-auto rounded-xl border border-white/[0.07] bg-[#191c1e] p-3.5">
            <div class="mb-2 flex items-center gap-2 text-xs font-bold text-ice-100"><Activity size={15} class="text-aqua-300" />{t('liveConsole')}</div>
            <p class="m-0 text-[11px] leading-relaxed text-ice-500">{lastAction() || t('waitingServer')}</p>
            <div class="mt-3 flex items-center gap-1.5 text-[10px] font-bold uppercase tracking-wider text-ice-500"><span class={`status-dot ${online() ? 'online' : ''}`}></span>{online() ? t('secureLink') : t('noGmLink')}</div>
          </div>
          <div class="mt-4 flex items-center justify-between px-2 text-[10px] text-ice-500"><span>{status()?.managed ? t('managedInstall') : t('localRepo')}</span><span>v{status()?.appVersion ?? '1.0.0'}</span></div>
        </aside>

        <main class="main-canvas relative flex min-w-0 flex-1 flex-col overflow-hidden">
          <header class="topbar flex h-[76px] shrink-0 items-center justify-between border-b border-white/[0.07] px-8">
            <div><div class="text-[10px] font-bold uppercase tracking-[.2em] text-ice-500">Kryuger Server / <span class="text-aqua-300">{t(route().label)}</span></div><h1 class="mt-1 text-[19px] font-bold tracking-wide">{t(route().title)}</h1></div>
            <div class="flex items-center gap-3">
              <div class="hidden items-center gap-2 rounded-lg border border-white/[0.07] bg-white/[0.025] px-3 py-2 text-[11px] text-ice-300 xl:flex"><Server size={14} class="text-aqua-300" /> http://127.0.0.1:5000</div>
              <div class={`flex items-center gap-2 rounded-lg border px-3 py-2 text-[11px] font-bold ${status()?.serverRunning ? 'border-aqua-400/20 bg-aqua-400/[0.07] text-aqua-300' : 'border-white/[0.08] bg-white/[0.025] text-ice-500'}`}><Wifi size={14} />{status()?.serverRunning ? t('online') : t('offline')}</div>
            </div>
          </header>

          <div class="content-scroll flex-1 overflow-y-auto px-8 py-7">
            <Show when={tab() === 'server'}><ServerPanel status={status()} refresh={refresh} notify={notify} /></Show>
            <Show when={tab() === 'overview'}><Overview status={status()} players={players() ?? []} catalog={catalog()} go={setTab} /></Show>
            <Show when={tab() === 'accounts'}><Accounts online={online()} players={players() ?? []} reload={reloadPlayers} notify={notify} /></Show>
            <Show when={tab() === 'operations'}><Operations online={online()} players={players() ?? []} catalog={catalog()} reload={reloadPlayers} notify={notify} /></Show>
          </div>
          <footer class="flex h-9 shrink-0 items-center justify-between border-t border-white/[0.06] px-8 text-[10px] text-ice-500">
            <span class="flex items-center gap-2"><span class={`status-dot ${online() ? 'online' : ''}`}></span>{online() ? t('gmActive') : t('gmInactive')} <span class="text-white/15">/</span> {t('loopbackOnly')}</span>
            <span class="flex items-center gap-1.5"><ShieldCheck size={12} />{t('protectedToken')}</span>
          </footer>
        </main>
        <Show when={notice()}>{(n) => (
          <div class={`toast ${n().error ? 'toast-error' : ''}`}><span class="toast-icon">{n().error ? <X size={15} /> : <Check size={15} />}</span><span class="min-w-0">{n().text}</span><button class="ml-auto text-ice-500 hover:text-white" onClick={() => setNotice(undefined)}><X size={14} /></button></div>
        )}</Show>
      </div>
    </I18n.Provider>
  );
}

// ------------------------------------------------------------------ server

function ServerPanel(props: { status?: Status; refresh: () => void; notify: (t: string, e?: boolean) => void }) {
  const t = useT();
  const [busy, setBusy] = createSignal(false);
  const [pct, setPct] = createSignal(0);
  const [step, setStep] = createSignal('');
  const [log, setLog] = createSignal('');
  const [gameDir, setGameDir] = createSignal('');
  let logBox: HTMLPreElement | undefined;

  onMount(async () => {
    const un = await listen<{ pct: number; message: string }>('manager-progress', (e) => { setPct(e.payload.pct); setStep(e.payload.message); });
    const tick = async () => { setLog(await invoke<string>('manager_log_tail', { lines: 250 })); if (logBox) logBox.scrollTop = logBox.scrollHeight; };
    void tick(); const timer = setInterval(tick, 2000);
    onCleanup(() => { un(); clearInterval(timer); });
  });

  const run = async (cmd: string, args?: Record<string, unknown>) => {
    setBusy(true);
    try { const r = await invoke<string | null>(cmd, args); if (r) props.notify(r); } catch (e) { props.notify(String(e), true); }
    setBusy(false); props.refresh();
  };
  const s = () => props.status;
  const chip = (v: boolean | undefined, yes: string, no: string) => <span class={`tiny-chip ${v ? '' : 'muted-chip'}`}>{v ? t(yes) : t(no)}</span>;

  return (
    <>
      <div class="mb-6 flex items-end justify-between">
        <div><h2 class="section-title">{t('serverTitle')}</h2><p class="section-subtitle">{t('serverHint')}</p></div>
        <button class="quiet-button" disabled={busy()} onClick={() => run('manager_update')}><RefreshCw size={14} /> {t('checkUpdate')}</button>
      </div>

      <section class="connect-card relative mb-6 overflow-hidden rounded-2xl border border-aqua-300/15 bg-ink-900 p-6">
        <div class="pointer-events-none absolute -right-10 -top-24 size-80 rounded-full bg-aqua-400/[0.09] blur-3xl"></div>
        <div class="relative flex items-center gap-6">
          <img src="/kryuger.png" alt="" class="size-24 rounded-2xl border border-aqua-300/30 object-cover shadow-[0_0_38px_#f2762b30]" />
          <div class="min-w-0 flex-1">
            <div class="mb-2 flex items-center gap-2 text-[10px] font-bold uppercase tracking-[.2em] text-aqua-300"><Power size={14} />{s()?.serverRunning ? t('running') : t('stopped')}</div>
            <h3 class="m-0 text-xl font-bold">{t('heroTitle')}</h3>
            <p class="mt-2 mb-0 text-[12px] text-ice-500">{step() || t('heroHint')}</p>
            <div class="mt-3 h-1.5 overflow-hidden rounded-full bg-white/[0.06]"><div class="h-full rounded-full bg-gradient-to-r from-[#ff9a55] to-[#e96922] transition-all" style={{ width: `${pct()}%` }}></div></div>
          </div>
          <div class="flex shrink-0 flex-col gap-2">
            <button class="primary-button" disabled={busy() || s()?.serverRunning} onClick={() => run('manager_start')}><Play size={15} /> {t('start')}</button>
            <button class="danger-button" disabled={busy() || !s()?.serverRunning} onClick={() => run('manager_stop')}><Square size={14} /> {t('stop')}</button>
            <button class="secondary-button" disabled={!s()?.serverRunning} onClick={() => run('manager_play')}><Gamepad2 size={15} /> {t('play')}</button>
          </div>
        </div>
      </section>

      <div class="mb-6 grid grid-cols-4 gap-4">
        <StatCard icon={Power} tone="aqua" label={t('state')} value={s()?.serverRunning ? t('online') : t('stopped2')} hint={t('ports')} />
        <StatCard icon={Cpu} tone="blue" label={t('build')} value={s()?.built ? t('ready') : t('pending')} hint=".NET 8 · Release" />
        <StatCard icon={RefreshCw} tone="amber" label={t('version')} value={s()?.managed && s()?.localCommit ? s()!.localCommit.slice(0, 7) : '-'} hint={s()?.managed ? t('installedCommit') : t('localCode')} />
        <StatCard icon={ShieldCheck} tone="violet" label={t('gmApi')} value={s()?.gmReady ? t('active') : t('inactive')} hint={t('loopbackOnly')} />
      </div>

      <div class="mb-6 grid grid-cols-[1.1fr_.9fr] gap-4">
        <section class="surface-card p-5">
          <h3 class="card-title">{t('installation')}</h3><p class="card-caption">{t('installationHint')}</p>
          <div class="mt-3">
            <div class="detail-row"><HardDrive size={15} class="text-aqua-300" /><span class="flex-1">{t('project')}</span><span class="max-w-[60%] truncate text-[11px] text-ice-500">{s()?.repoRoot}</span></div>
            <div class="detail-row"><HardDrive size={15} class="text-aqua-300" /><span class="flex-1">{t('playerData')}</span><span class="max-w-[60%] truncate text-[11px] text-ice-500">{s()?.dataDir}</span></div>
            <div class="detail-row"><Gamepad2 size={15} class="text-aqua-300" /><span class="flex-1">{t('redirect')}</span>{chip(s()?.hookInstalled, 'installed', 'removed')}</div>
            <div class="detail-row"><Cpu size={15} class="text-aqua-300" /><span class="flex-1">{t('serverBuilt')}</span>{chip(s()?.built, 'ready', 'pending')}</div>
          </div>
          <div class="mt-4 flex gap-2">
            <button class="quiet-button" onClick={() => invoke('manager_open_folder', { kind: 'root' })}><FolderOpen size={14} /> {t('project')}</button>
            <button class="quiet-button" onClick={() => invoke('manager_open_folder', { kind: 'data' })}><FolderOpen size={14} /> {t('data')}</button>
            <button class="quiet-button" onClick={() => invoke('manager_open_folder', { kind: 'logs' })}><FolderOpen size={14} /> {t('logs')}</button>
          </div>
        </section>
        <section class="surface-card p-5">
          <h3 class="card-title">{t('game')}</h3><p class="card-caption">{t('gameHint')}</p>
          <div class="mt-3 detail-row"><Gamepad2 size={15} class="text-aqua-300" /><span class="flex-1 truncate text-[12px]">{s()?.gameDir ?? t('notDetected')}</span></div>
          <label class="field-label mt-4">{t('otherFolder')}
            <div class="flex gap-2"><input class="input-field flex-1" placeholder="C:\...\GIRLS' FRONTLINE EN" value={gameDir()} onInput={(e) => setGameDir(e.currentTarget.value)} />
              <button class="secondary-button" onClick={() => run('manager_set_game_dir', { gameDir: gameDir() })}>{t('save')}</button></div>
          </label>
        </section>
      </div>

      <section class="surface-card p-5">
        <div class="mb-3 flex items-center gap-2"><Activity size={15} class="text-aqua-300" /><h3 class="card-title">{t('serverLog')}</h3></div>
        <pre class="command-output" ref={logBox}>{log() || t('noActivity')}</pre>
      </section>
    </>
  );
}

// ------------------------------------------------------------------ overview

function Overview(props: { status?: Status; players: Player[]; catalog?: Catalog; go: (tab: Tab) => void }) {
  const t = useT();
  const totalGuns = () => props.players.reduce((a, p) => a + p.guns, 0);
  return (
    <>
      <div class="mb-6"><h2 class="section-title">{t('serverState')}</h2><p class="section-subtitle">{t('serverStateHint')}</p></div>
      <div class="mb-6 grid grid-cols-4 gap-4">
        <StatCard icon={Users} tone="aqua" label={t('accounts')} value={String(props.players.length)} hint={t('inDatabase')} />
        <StatCard icon={Sparkles} tone="violet" label={t('dolls')} value={String(totalGuns())} hint={t('inAllAccounts')} />
        <StatCard icon={Boxes} tone="amber" label={t('catalog')} value={props.catalog ? String(props.catalog.guns.length + props.catalog.items.length + props.catalog.equips.length) : '-'} hint={t('catalogHint')} />
        <StatCard icon={Gauge} tone="blue" label={t('server')} value={props.status?.serverRunning ? t('online') : t('stopped2')} hint="127.0.0.1:5000" />
      </div>
      <section class="surface-card p-5">
        <h3 class="card-title">{t('quickAccess')}</h3><p class="card-caption mb-4">{t('quickAccessHint')}</p>
        <QuickRow icon={Server} title={t('qServer')} subtitle={t('qServerHint')} onClick={() => props.go('server')} />
        <QuickRow icon={Users} title={t('qAccounts')} subtitle={t('qAccountsHint')} onClick={() => props.go('accounts')} amber />
        <QuickRow icon={Gift} title={t('qGive')} subtitle={t('qGiveHint')} onClick={() => props.go('operations')} violet />
      </section>
    </>
  );
}

// ------------------------------------------------------------------ accounts

function Accounts(props: { online: boolean; players: Player[]; reload: () => void; notify: (t: string, e?: boolean) => void }) {
  const t = useT();
  const [query, setQuery] = createSignal('');
  const [selected, setSelected] = createSignal<number>();
  const list = createMemo(() => { const q = query().trim().toLowerCase(); return props.players.filter((p) => !q || String(p.id).includes(q) || p.name.toLowerCase().includes(q)); });
  const player = () => props.players.find((p) => p.id === selected());
  return (
    <Show when={props.online} fallback={<Offline />}>
      <div class="mb-5"><h2 class="section-title">{t('titleAccounts')}</h2><p class="section-subtitle">{t('accountsHint')}</p></div>
      <div class="grid grid-cols-[.9fr_1.1fr] gap-4">
        <section class="surface-card overflow-hidden">
          <div class="flex gap-2 border-b border-white/[0.06] p-4">
            <div class="search-field"><Search size={15} /><input placeholder={t('uidOrName')} value={query()} onInput={(e) => setQuery(e.currentTarget.value)} /></div>
            <button class="quiet-button" onClick={() => props.reload()}><RefreshCw size={14} /></button>
          </div>
          <div class="max-h-[520px] overflow-auto">
            <Show when={list().length} fallback={<div class="empty-state">{t('noAccounts')}</div>}>
              <For each={list()}>{(p) => (
                <button class={`account-row ${selected() === p.id ? 'selected' : ''}`} onClick={() => setSelected(p.id)}>
                  <span class="avatar">{(p.name || '?').slice(0, 2).toUpperCase()}</span>
                  <span class="min-w-0 flex-1 text-left"><b>{p.name || 'Commander'}</b><small>UID {p.id} · {t('level')} {p.level}</small></span>
                  <ArrowRight size={14} class="text-ice-500" />
                </button>)}</For>
            </Show>
          </div>
        </section>
        <section class="surface-card p-5">
          <Show when={player()} fallback={<div class="empty-state">{t('pickAccount')}</div>}>{(p) => (
            <>
              <div class="mb-4 flex items-center gap-3"><span class="avatar size-12 text-sm">{(p().name || '?').slice(0, 2).toUpperCase()}</span><div><h3 class="card-title text-base">{p().name || 'Commander'}</h3><p class="card-caption">UID {p().id} · Steam {p().openId}</p></div></div>
              <div class="mb-4 grid grid-cols-4 gap-2">
                <Metric label={t('level')} value={p().level} /><Metric label={t('gems')} value={p().gem} /><Metric label={t('dolls')} value={p().guns} /><Metric label={t('equipment')} value={p().equips} />
                <Metric label={t('manpower')} value={p().mp} /><Metric label={t('ammo')} value={p().ammo} /><Metric label={t('rations')} value={p().mre} /><Metric label={t('parts')} value={p().part} />
              </div>
              <div class="detail-row"><Sparkles size={15} class="text-aqua-300" /><span class="flex-1">{t('fairies')}</span><b>{p().fairies}</b></div>
              <AccountEditor player={p()} reload={props.reload} notify={props.notify} onDeleted={() => setSelected(undefined)} />
            </>)}</Show>
        </section>
      </div>
    </Show>
  );
}

const EDIT_FIELDS: { key: string; label: string; get: (p: Player) => string | number }[] = [
  { key: 'name', label: 'name', get: (p) => p.name }, { key: 'level', label: 'level', get: (p) => p.level },
  { key: 'experience', label: 'experience', get: (p) => p.experience }, { key: 'gem', label: 'gems', get: (p) => p.gem },
  { key: 'mp', label: 'manpower', get: (p) => p.mp }, { key: 'ammo', label: 'ammo', get: (p) => p.ammo },
  { key: 'mre', label: 'rations', get: (p) => p.mre }, { key: 'part', label: 'parts', get: (p) => p.part },
  { key: 'maxgun', label: 'maxGun', get: (p) => p.maxGun }, { key: 'maxequip', label: 'maxEquip', get: (p) => p.maxEquip },
  { key: 'maxteam', label: 'maxTeam', get: (p) => p.maxTeam },
];

function AccountEditor(props: { player: Player; reload: () => void; notify: (t: string, e?: boolean) => void; onDeleted: () => void }) {
  const t = useT();
  const [draft, setDraft] = createSignal<Record<string, string>>({});
  const [confirming, setConfirming] = createSignal(false);
  const [busy, setBusy] = createSignal(false);
  const value = (f: (typeof EDIT_FIELDS)[number]) => draft()[f.key] ?? String(f.get(props.player) ?? '');

  const save = async (e: Event) => {
    e.preventDefault();
    const changed = EDIT_FIELDS.filter((f) => draft()[f.key] !== undefined && draft()[f.key] !== String(f.get(props.player) ?? ''));
    if (!changed.length) return props.notify(t('noChanges'));
    setBusy(true);
    try {
      for (const f of changed) await gm('POST', '/gm/set', { uid: props.player.id, field: f.key, value: draft()[f.key] });
      setDraft({});
      props.notify(`${t('account')} ${props.player.id}: ${t('saved')}`);
      props.reload();
    } catch (err) { props.notify(String(err), true); }
    finally { setBusy(false); }
  };
  const remove = async () => {
    if (!confirming()) return setConfirming(true);
    setBusy(true);
    try {
      await gm('POST', '/gm/delete', { uid: props.player.id });
      props.notify(`${t('account')} ${props.player.id}: ${t('deleted')}`);
      setConfirming(false);
      props.onDeleted();
      props.reload();
    } catch (err) { props.notify(String(err), true); }
    finally { setBusy(false); }
  };

  return (
    <form class="mt-4 border-t border-white/[0.06] pt-4" onSubmit={save}>
      <h4 class="card-title mb-3 text-sm">{t('editAccount')}</h4>
      <div class="grid grid-cols-3 gap-2">
        <For each={EDIT_FIELDS}>{(f) => (
          <Field label={t(f.label)}><input class="input-field" value={value(f)} onInput={(e) => setDraft({ ...draft(), [f.key]: e.currentTarget.value })} /></Field>
        )}</For>
      </div>
      <div class="mt-4 flex gap-2">
        <button class="primary-button flex-1" type="submit" disabled={busy()}><Save size={15} /> {t('saveChanges')}</button>
        <button class="quiet-button" type="button" disabled={busy()} onClick={remove} style={confirming() ? 'color:#f87171;border-color:#f8717166' : ''}>
          <Trash2 size={15} /> {confirming() ? t('confirmDelete') : t('deleteAccount')}
        </button>
        <Show when={confirming()}><button class="quiet-button" type="button" onClick={() => setConfirming(false)}>{t('cancel')}</button></Show>
      </div>
    </form>
  );
}

// ------------------------------------------------------------------ operations

const KINDS: { key: string; label: string; list?: keyof Catalog }[] = [
  { key: 'gun', label: 'kGun', list: 'guns' }, { key: 'item', label: 'kItem', list: 'items' },
  { key: 'equip', label: 'kEquip', list: 'equips' }, { key: 'fairy', label: 'kFairy', list: 'fairies' },
  { key: 'skin', label: 'kSkin', list: 'skins' }, { key: 'gem', label: 'kGem' }, { key: 'resources', label: 'kResources' },
];

function Operations(props: { online: boolean; players: Player[]; catalog?: Catalog; reload: () => void; notify: (t: string, e?: boolean) => void }) {
  const t = useT();
  const [uid, setUid] = createSignal('');
  const [kind, setKind] = createSignal('gun');
  const [picked, setPicked] = createSignal<Entry>();
  const [count, setCount] = createSignal('1');
  const [all, setAll] = createSignal(false);
  const [setUidV, setSetUid] = createSignal('');
  const [field, setField] = createSignal('gem');
  const [value, setValue] = createSignal('');
  const kindInfo = () => KINDS.find((k) => k.key === kind())!;
  const entries = () => (kindInfo().list && props.catalog ? props.catalog[kindInfo().list!] : []);

  const give = async (e: Event) => {
    e.preventDefault();
    if (!uid()) return props.notify(t('needUid'), true);
    const everything = !!kindInfo().list && all();
    if (kindInfo().list && !everything && !picked()) return props.notify(t('needPick'), true);
    try { const r = await gm<{ message: string }>('POST', '/gm/give', { uid: +uid(), kind: kind(), id: everything ? 0 : picked()?.id ?? 0, count: Math.max(1, +count() || 1) }); props.notify(`${t('granted')} ${uid()}: ${r.message}`); props.reload(); }
    catch (err) { props.notify(String(err), true); }
  };
  const apply = async (e: Event) => {
    e.preventDefault();
    if (!setUidV()) return props.notify(t('needUid'), true);
    try { const r = await gm<{ message: string }>('POST', '/gm/set', { uid: +setUidV(), field: field(), value: value() }); props.notify(`${t('account')} ${setUidV()}: ${r.message}`); props.reload(); }
    catch (err) { props.notify(String(err), true); }
  };

  return (
    <Show when={props.online} fallback={<Offline />}>
      <div class="mb-5"><h2 class="section-title">{t('titleOperations')}</h2><p class="section-subtitle">{t('operationsHint')}</p></div>
      <div class="grid grid-cols-2 gap-4">
        <form class="surface-card operation-card" onSubmit={give}>
          <OperationHeading icon={Gift} tone="aqua" number="01" title={t('give')} subtitle={t('giveHint')} />
          <div class="form-grid">
            <Field label={t('accountUid')}><AccountInput value={uid()} set={setUid} players={props.players} /></Field>
            <Field label={t('type')}><select class="input-field" value={kind()} onChange={(e) => { setKind(e.currentTarget.value); setPicked(undefined); }}><For each={KINDS}>{(k) => <option value={k.key}>{t(k.label)}</option>}</For></select></Field>
          </div>
          <Show when={kindInfo().list}>
            <label class="mb-3 flex cursor-pointer items-center gap-2 text-sm text-ice-200">
              <input type="checkbox" checked={all()} onChange={(e) => setAll(e.currentTarget.checked)} /> {t('giveAll')}
            </label>
            <Show when={!all()} fallback={<p class="card-caption mb-3">{t(`giveAllHint_${kind()}`)}</p>}>
              <Field label={`${t('search')}: ${t(kindInfo().label)}`}><CatalogPicker entries={entries()} picked={picked()} pick={setPicked} /></Field>
            </Show>
          </Show>
          <Field label={kind() === 'gem' || kind() === 'resources' || (all() && kind() === 'item') ? t('amount') : t('copies')}><input class="input-field" inputmode="numeric" value={count()} onInput={(e) => setCount(e.currentTarget.value)} /></Field>
          <button class="primary-button w-full" type="submit"><Gift size={15} /> {t('give')}</button>
        </form>

        <form class="surface-card operation-card" onSubmit={apply}>
          <OperationHeading icon={SlidersHorizontal} tone="amber" number="02" title={t('setValue')} subtitle={t('setValueHint')} />
          <div class="form-grid">
            <Field label={t('accountUid')}><AccountInput value={setUidV()} set={setSetUid} players={props.players} /></Field>
            <Field label={t('field')}><select class="input-field" value={field()} onChange={(e) => setField(e.currentTarget.value)}>
              <option value="gem">{t('gems')}</option><option value="mp">{t('manpower')}</option><option value="ammo">{t('ammo')}</option><option value="mre">{t('rations')}</option>
              <option value="part">{t('parts')}</option><option value="level">{t('level')}</option><option value="name">{t('name')}</option></select></Field>
          </div>
          <Field label={t('newValue')}><input class="input-field" value={value()} onInput={(e) => setValue(e.currentTarget.value)} /></Field>
          <button class="primary-button w-full" type="submit"><Coins size={15} /> {t('apply')}</button>
        </form>
      </div>
    </Show>
  );
}

function AccountInput(props: { value: string; set: (v: string) => void; players: Player[] }) {
  return <>
    <input class="input-field" list="kryuger-players" inputmode="numeric" placeholder="100001" value={props.value} onInput={(e) => props.set(e.currentTarget.value)} />
    <datalist id="kryuger-players"><For each={props.players}>{(p) => <option value={String(p.id)}>{p.name}</option>}</For></datalist>
  </>;
}

function CatalogPicker(props: { entries: Entry[]; picked?: Entry; pick: (e: Entry) => void }) {
  const t = useT();
  const [query, setQuery] = createSignal('');
  const [open, setOpen] = createSignal(false);
  const results = createMemo(() => { const q = query().trim().toLowerCase(); return props.entries.filter((e) => !q || e.name.toLowerCase().includes(q) || String(e.id) === q).slice(0, 150); });
  return (
    <div class={`catalog-picker ${open() ? 'catalog-picker-open' : ''}`}>
      <div class="catalog-input-wrap"><Search size={15} />
        <input placeholder={props.picked ? `${props.picked.name} (#${props.picked.id})` : t('nameOrId')} value={query()} onFocus={() => setOpen(true)} onInput={(e) => { setQuery(e.currentTarget.value); setOpen(true); }} onBlur={() => setTimeout(() => setOpen(false), 150)} />
        <button type="button" class="catalog-toggle" onClick={() => setOpen(!open())}><span class={`catalog-chevron ${open() ? 'expanded' : ''}`}></span></button>
      </div>
      <Show when={open()}>
        <div class="catalog-dropdown">
          <div class="catalog-result-count">{results().length} {t('results')}</div>
          <Show when={results().length} fallback={<div class="catalog-empty">{t('noMatches')}</div>}>
            <For each={results()}>{(e) => (
              <button type="button" class={`catalog-option ${props.picked?.id === e.id ? 'selected' : ''}`} onMouseDown={() => { props.pick(e); setQuery(''); setOpen(false); }}>
                <span class="catalog-option-image">{e.rank ? `R${e.rank}` : '#'}</span>
                <span class="catalog-option-copy"><b>{e.name}</b><small>ID {e.id}{e.rank ? ` · ${t('rank')} ${e.rank}` : ''}{e.gun ? ` · ${t('forDoll')} ${e.gun}` : ''}</small></span>
              </button>)}</For>
          </Show>
        </div>
      </Show>
      <Show when={props.picked}>{(p) => <div class="selection-summary"><Check size={14} class="text-aqua-300" />{p().name}<small>#{p().id}</small></div>}</Show>
    </div>
  );
}

// ------------------------------------------------------------------ small pieces

function Offline() {
  const t = useT();
  return <div class="empty-state surface-card"><Server size={22} class="text-aqua-300" /><b class="text-ice-100">{t('offlineTitle')}</b>{t('offlineHint')}</div>;
}
function StatCard(props: { icon: typeof Gauge; label: string; value: string; hint: string; tone: string }) {
  const Icon = props.icon;
  return <div class="stat-card"><div class="flex items-start justify-between"><span class={`stat-icon ${props.tone}`}><Icon size={17} /></span><span class="stat-signal"><i></i><i></i><i></i><i></i></span></div>
    <div class="mt-4 text-[10px] font-bold uppercase tracking-[.16em] text-ice-500">{props.label}</div><div class="mt-1 text-[26px] font-bold tracking-tight text-ice-100">{props.value}</div><div class="mt-1 text-[11px] text-ice-500">{props.hint}</div></div>;
}
function Field(props: { label: string; children: JSX.Element }) { return <label class="field-label">{props.label}{props.children}</label>; }
function Metric(props: { label: string; value: number }) { return <div class="metric-tile"><span>{props.label}</span><b>{props.value.toLocaleString()}</b></div>; }
function OperationHeading(props: { icon: typeof Gift; title: string; subtitle: string; number: string; tone: string }) {
  const Icon = props.icon;
  return <div class="operation-heading"><span class={`operation-icon ${props.tone}`}><Icon size={18} /></span><div><h3>{props.title}</h3><p>{props.subtitle}</p></div><span class="operation-number">{props.number}</span></div>;
}
function QuickRow(props: { icon: typeof Users; title: string; subtitle: string; onClick: () => void; amber?: boolean; violet?: boolean }) {
  const Icon = props.icon;
  return <button class="quick-row" onClick={props.onClick}><span class={`quick-icon ${props.amber ? 'amber' : props.violet ? 'violet' : ''}`}><Icon size={17} /></span><span><b>{props.title}</b><small>{props.subtitle}</small></span><ArrowRight size={15} class="ml-auto text-ice-500" /></button>;
}
