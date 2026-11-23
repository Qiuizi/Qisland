// Qisland v2 · 正式版交互：时钟、可交互顶栏岛、导航与圆点同步、停靠切换、Codex/DeepSeek/Claude 审批演示、推送接口模拟
(function () {
  'use strict';

  var $ = function (s, r) { return (r || document).querySelector(s); };
  var $$ = function (s, r) { return Array.prototype.slice.call((r || document).querySelectorAll(s)); };
  var esc = function (s) {
    return String(s).replace(/[&<>"]/g, function (c) {
      return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c];
    });
  };

  var root = document.documentElement;
  var reduce = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  var island = $('#island');
  var topBar = $('#top');
  var hero = $('#hero');
  var heroIsland = $('#heroIsland');
  var heroStickers = $('#heroStickers');

  /* ---------- 时钟（本机时间） ---------- */
  function tick() {
    var d = new Date();
    var s = String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0');
    $$('[data-clock]').forEach(function (el) { el.textContent = s; });
  }
  tick();
  setInterval(tick, 15000);

  /* ---------- 顶栏岛可访问性与激活态同步 ---------- */
  function syncIslandInteract() {
    if (!island) return;
    var active = island.classList.contains('on');
    var st = island.dataset.state || 'idle';
    $$('.isl', island).forEach(function (el) {
      var cur = active && el.dataset.v === st;
      el.classList.toggle('is-current', cur);
      $$('button', el).forEach(function (btn) {
        btn.tabIndex = cur ? 0 : -1;
        btn.setAttribute('aria-hidden', cur ? 'false' : 'true');
      });
    });
  }

  /* ---------- 首屏胶囊滚动（无布局读取） ---------- */
  var heroH = hero ? hero.offsetHeight : 700;
  function measureHero() {
    if (hero) heroH = hero.offsetHeight || 700;
  }
  if (hero && 'ResizeObserver' in window) {
    new ResizeObserver(measureHero).observe(hero);
  } else {
    window.addEventListener('resize', measureHero, { passive: true });
  }

  var ticking = false;
  var lastOn = null;
  var lastScrolled = null;
  function onScroll() {
    ticking = false;
    var y = window.scrollY;
    var p = Math.min(1, Math.max(0, y / (heroH * 0.45)));
    if (!reduce) {
      var v = p.toFixed(3);
      if (heroIsland) heroIsland.style.setProperty('--hp', v);
      if (heroStickers) heroStickers.style.setProperty('--hp', v);
    }
    var isOn = reduce ? y > heroH * 0.35 : p > 0.72;
    if (isOn !== lastOn) {
      island.classList.toggle('on', isOn);
      lastOn = isOn;
      syncIslandInteract();
    }
    var isScrolled = y > 12;
    if (isScrolled !== lastScrolled) {
      topBar.classList.toggle('scrolled', isScrolled);
      lastScrolled = isScrolled;
    }
  }
  window.addEventListener('scroll', function () {
    if (!ticking) {
      ticking = true;
      requestAnimationFrame(onScroll);
    }
  }, { passive: true });
  onScroll();

  /* ---------- 顶栏岛状态、导航高亮与右侧章节圆点 ---------- */
  var currentSectionState = 'idle';
  var currentSectionId = 'hero';
  var flashTimer = 0;

  function setIslandState(st) {
    if (!island) return;
    island.dataset.state = st;
    syncIslandInteract();
  }

  function flashIsland(state, ms) {
    clearTimeout(flashTimer);
    setIslandState(state);
    if (ms) {
      flashTimer = setTimeout(function () {
        flashTimer = 0;
        setIslandState(currentSectionState);
      }, ms);
    }
  }

  var navLinks = $$('.nav a[href^="#"]');
  var railDots = $$('.rail-dot');
  var navMap = {
    hero: '',
    features: '#features',
    music: '#features',
    agent: '#agent',
    homework: '#features',
    schedule: '#features',
    api: '#api',
    glass: '#features',
    small: '#features',
    perf: '#features',
    privacy: '#privacy',
    film: '#privacy',
    download: ''
  };

  function syncNavHighlight(secId) {
    currentSectionId = secId || 'hero';
    var targetHref = navMap[currentSectionId] || '';
    navLinks.forEach(function (a) {
      if (a.getAttribute('href') === targetHref) a.setAttribute('aria-current', 'location');
      else a.removeAttribute('aria-current');
    });
    railDots.forEach(function (dot) {
      if (dot.dataset.sec === currentSectionId) dot.setAttribute('aria-current', 'location');
      else dot.removeAttribute('aria-current');
    });
  }

  function smoothScrollTo(el) {
    if (!el) return;
    el.scrollIntoView({ behavior: reduce ? 'auto' : 'smooth', block: 'center' });
  }

  // 监测所有带 id 的章节（用于导航/圆点高亮及顶栏岛状态）
  var allBands = $$('main > section[id]');
  if ('IntersectionObserver' in window) {
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (e) {
        if (e.isIntersecting) {
          var id = e.target.id;
          syncNavHighlight(id);
          if (e.target.dataset.island) {
            currentSectionState = e.target.dataset.island;
            if (!flashTimer) setIslandState(currentSectionState);
          }
        }
      });
    }, { rootMargin: '-42% 0px -54% 0px' });
    allBands.forEach(function (s) { io.observe(s); });

    var rv = new IntersectionObserver(function (entries) {
      entries.forEach(function (e) {
        if (e.isIntersecting || e.boundingClientRect.top < window.innerHeight) {
          e.target.classList.add('in');
          rv.unobserve(e.target);
        }
      });
    }, { rootMargin: '0px 0px 40px 0px' });
    $$('.rv, .tiers, .dock').forEach(function (el) { rv.observe(el); });
  } else {
    $$('.rv, .tiers, .dock').forEach(function (el) { el.classList.add('in'); });
  }
  syncNavHighlight('hero');

  requestAnimationFrame(function () {
    setTimeout(function () { root.classList.add('loaded'); }, reduce ? 0 : 280);
  });

  /* ---------- 无障碍 Tab 键盘导航 ---------- */
  function bindTablist(tablistSelector, onSelect) {
    var list = $(tablistSelector);
    if (!list) return;
    var tabs = $$('[role="tab"]', list);
    tabs.forEach(function (tab, idx) {
      tab.addEventListener('click', function () {
        tabs.forEach(function (t) { t.setAttribute('aria-selected', String(t === tab)); });
        onSelect(tab);
      });
      tab.addEventListener('keydown', function (e) {
        var next = -1;
        if (e.key === 'ArrowRight') next = (idx + 1) % tabs.length;
        else if (e.key === 'ArrowLeft') next = (idx - 1 + tabs.length) % tabs.length;
        else if (e.key === 'Home') next = 0;
        else if (e.key === 'End') next = tabs.length - 1;
        if (next >= 0) {
          e.preventDefault();
          tabs[next].focus();
          tabs[next].click();
        }
      });
    });
  }

  /* ---------- 1. Codex / DeepSeek Harness / Claude Code 100 秒审批演示 ---------- */
  var AG = {
    codex: {
      name: 'Codex',
      title: 'PowerShell · codex',
      src: 'Codex 请求执行命令',
      cmd: 'dotnet test tests/Qisland.Core.Tests',
      ask: [
        '<span class="p">PS&gt;</span> codex',
        '<span class="d">› 跑一下单元测试</span>',
        '• 需要执行：dotnet test tests/Qisland.Core.Tests',
        '<span class="w">⏳ 等待批准 · 已推到 Qisland</span>'
      ]
    },
    dsh: {
      name: 'DeepSeek Harness',
      title: 'PowerShell · DeepSeek Harness',
      src: 'DeepSeek Harness 请求执行命令',
      cmd: 'npm run build',
      ask: [
        '<span class="p">PS&gt;</span> dsh',
        '<span class="d">› 打包一下前端</span>',
        '• 需要执行：npm run build',
        '<span class="w">⏳ 等待批准 · 已推到 Qisland</span>'
      ]
    },
    claude: {
      name: 'Claude Code',
      title: 'PowerShell · claude',
      src: 'Claude Code 等待确认',
      cmd: '请在终端窗口确认（岛上仅作提醒，不支持允许或拒绝）',
      ask: [
        '<span class="p">PS&gt;</span> claude',
        '<span class="d">› 整理一下 README</span>',
        '• 想要编辑 README.md',
        '<span class="term-yn" data-term-yn><span class="w">? 是否允许编辑 README.md [y/N]</span></span>'
      ]
    }
  };

  var approveBox = $('#approveBox');
  var apCard = $('#apCard');
  var termBody = $('#termBody');
  var apSec = $('#apSec');
  var apLeft = $('#apLeft');
  var ringArc = $('#ringArc');
  var ringBox = $('.ring', apCard);
  var apBtns = $('#apBtns');
  var btnAllow = $('[data-act="allow"]', apBtns);
  var btnDeny = $('[data-act="deny"]', apBtns);
  var btnTerm = $('[data-act="term"]', apBtns);
  var apResult = $('#apResult');
  var apReset = $('#apReset');
  var approveGrid = $('#approveGrid');
  var agent = 'codex';
  var left = 100;
  var cd = 0;
  var apVisible = false;
  var countdownStarted = false;
  var finished = false;
  var C = 97.4;

  function renderTerm(extra, highlightYn) {
    if (!termBody) return;
    var lines = AG[agent].ask.slice();
    if (extra && extra.length) lines = lines.concat(extra);
    termBody.innerHTML = lines.join('\n');
    if (highlightYn) {
      var yn = $('[data-term-yn]', termBody);
      if (yn) {
        yn.classList.add('hl-yn');
        var caret = document.createElement('span');
        caret.className = 'caret';
        caret.setAttribute('aria-hidden', 'true');
        yn.appendChild(document.createTextNode(' '));
        yn.appendChild(caret);
      }
    }
  }

  function setRing() {
    if (ringArc) ringArc.style.strokeDashoffset = String(C * (1 - left / 100));
    if (apSec) apSec.textContent = String(left);
  }

  function ensureCountdown() {
    if (cd || finished || agent === 'claude' || !apVisible) return;
    countdownStarted = true;
    cd = setInterval(function () {
      if (!apVisible || document.hidden || finished || agent === 'claude') return;
      left -= 1;
      setRing();
      if (left <= 0) finishAgent('timeout');
    }, 1000);
  }

  function startAgent(a) {
    agent = a;
    finished = false;
    left = 100;
    clearInterval(cd);
    cd = 0;
    var A = AG[a];
    $('#termTitle').textContent = A.title;
    $('#apSrc').textContent = A.src;
    $('#apCmd').textContent = A.cmd;
    if (approveGrid) approveGrid.setAttribute('aria-labelledby', 'tab-' + a);

    var isClaude = a === 'claude';
    apBtns.hidden = false;
    if (btnAllow) btnAllow.hidden = isClaude;
    if (btnDeny) btnDeny.hidden = isClaude;
    if (btnTerm) btnTerm.hidden = false;
    apLeft.hidden = isClaude;
    if (ringBox) ringBox.style.visibility = isClaude ? 'hidden' : 'visible';
    apResult.hidden = !isClaude;
    apReset.hidden = true;

    if (isClaude) {
      apResult.className = 'ap-result back';
      apResult.textContent = '岛上只弹出等待提示；点击「回终端」可定位到终端里的 [y/N] 确认行。';
    } else {
      apResult.className = 'ap-result';
      apResult.textContent = '';
    }
    renderTerm(null, false);
    setRing();

    if (!isClaude && apVisible) ensureCountdown();
  }

  function finishAgent(act) {
    finished = true;
    clearInterval(cd);
    cd = 0;
    apResult.hidden = false;
    apReset.hidden = false;
    var n = AG[agent].name;
    if (act === 'allow') {
      apResult.className = 'ap-result';
      apResult.textContent = '已允许 · ' + n + ' 继续运行';
      renderTerm(['<span class="ok">✓ 已批准（来自 Qisland）</span>', '<span class="d">  正在运行…</span>']);
      flashIsland('allowed', 2600);
    } else if (act === 'deny') {
      apResult.className = 'ap-result no';
      apResult.textContent = '已拒绝 · 命令没有执行';
      renderTerm(['<span class="no">✕ 已拒绝，命令没有执行</span>']);
      flashIsland('denied', 2600);
    } else if (agent === 'claude') {
      apResult.className = 'ap-result back';
      apResult.textContent = '已在左侧终端高亮 [y/N] 确认行，请在终端中输入 y 或 n。';
      renderTerm(null, true);
      flashIsland('back', 2600);
    } else {
      apResult.className = 'ap-result back';
      apResult.textContent = act === 'timeout'
        ? '100 秒没点 · 已交回终端，从不默认同意'
        : '已交回终端，在那边确认';
      renderTerm(['<span class="term-yn hl-yn"><span class="w">? 是否允许执行该命令 [y/N]</span> <span class="caret" aria-hidden="true"></span></span>']);
      flashIsland('back', 2600);
    }
  }

  if (apCard) {
    $$('[data-act]', apBtns).forEach(function (b) {
      b.addEventListener('click', function () { finishAgent(b.dataset.act); });
    });
    apReset.addEventListener('click', function () { startAgent(agent); });
    bindTablist('.approve-head [role="tablist"]', function (tab) {
      startAgent(tab.dataset.agent);
    });
    if ('IntersectionObserver' in window) {
      new IntersectionObserver(function (es) {
        es.forEach(function (e) {
          apVisible = e.isIntersecting;
          if (apVisible && !countdownStarted && !finished && agent !== 'claude') {
            ensureCountdown();
          } else if (apVisible && countdownStarted && !finished && agent !== 'claude') {
            ensureCountdown();
          }
        });
      }, { threshold: 0.2 }).observe(approveBox || apCard);
    } else {
      apVisible = true;
    }
    startAgent('codex');
  }

  /* ---------- 顶栏岛点击交互（Plan A：拒/允联动审批区；其他态跳到对应区域） ---------- */
  if (island) {
    $$('[data-island-act]', island).forEach(function (btn) {
      btn.addEventListener('click', function () {
        var act = btn.dataset.islandAct;
        if (agent === 'claude') {
          var codexTab = $('#tab-codex');
          if (codexTab) {
            $$('.approve-head [role="tab"]').forEach(function (t) {
              t.setAttribute('aria-selected', String(t === codexTab));
            });
          }
          startAgent('codex');
        }
        finishAgent(act);
        smoothScrollTo(approveBox || $('#agent'));
      });
    });

    $$('[data-island-jump]', island).forEach(function (btn) {
      btn.addEventListener('click', function () {
        var id = btn.dataset.islandJump;
        smoothScrollTo(document.getElementById(id));
      });
    });
  }

  /* ---------- 停靠位置切换器（自动轮播 + 手动点击永久锁定） ---------- */
  var dockBox = $('#dockBox');
  if (dockBox) {
    var dockBtns = $$('.dock-btn', dockBox);
    var dockPills = {
      top: $('.d-top', dockBox),
      left: $('.d-left', dockBox),
      right: $('.d-right', dockBox)
    };
    var dockOrder = ['top', 'left', 'right'];
    var dockIdx = 0;
    var dockLocked = false;
    var dockTimer = 0;

    function applyDock(pos) {
      dockBox.dataset.dockPos = pos;
      dockBtns.forEach(function (b) {
        b.setAttribute('aria-pressed', String(b.dataset.dock === pos));
      });
      dockOrder.forEach(function (k) {
        if (dockPills[k]) dockPills[k].classList.toggle('is-active', k === pos);
      });
    }

    dockBtns.forEach(function (b) {
      b.addEventListener('click', function () {
        dockLocked = true;
        if (dockTimer) { clearInterval(dockTimer); dockTimer = 0; }
        applyDock(b.dataset.dock);
      });
    });

    applyDock('top');
    if (!reduce) {
      dockTimer = setInterval(function () {
        if (dockLocked || document.hidden) return;
        dockIdx = (dockIdx + 1) % dockOrder.length;
        applyDock(dockOrder[dockIdx]);
      }, 1700);
    }
  }

  /* ---------- 2. 本机推送接口示例面板（curl / PowerShell / WSL2） ---------- */
  var s = function (t) { return '<span class="tk-s">' + esc(t) + '</span>'; };
  var c = function (t) { return '<span class="tk-c">' + esc(t) + '</span>'; };
  var k = function (t) { return '<span class="tk-k">' + esc(t) + '</span>'; };
  var f = function (t) { return '<span class="tk-f">' + esc(t) + '</span>'; };

  var CODE = {
    curl: {
      note: '来自仓库 README · Island API',
      lines: [
        c('# 推一张卡片（同 id 再推就是更新）'),
        k('curl') + f(' -X POST ') + 'http://127.0.0.1:9840/v1/island/push \\',
        f('  -d ') + s('\'{"id":"build","title":"构建中","body":"step 3/5","progress":0.6,"type":"info"}\''),
        '',
        c('# 构建完成：还是同一个 id'),
        k('curl') + f(' -X POST ') + 'http://127.0.0.1:9840/v1/island/push \\',
        f('  -d ') + s('\'{"id":"build","title":"构建完成","type":"success","duration_seconds":8}\''),
        '',
        c('# 收回'),
        k('curl') + f(' -X DELETE ') + 'http://127.0.0.1:9840/v1/island/push/build'
      ],
      steps: [[0, 1, 2], [4, 5, 6], [8, 9]]
    },
    ps: {
      note: 'PowerShell · 推送与收回同一张构建卡片',
      lines: [
        c('# 推一张卡片（同 id 再推就是更新）'),
        k('$card') + ' = @{ id = ' + s('"build"') + '; title = ' + s('"构建中"') + '; body = ' + s('"step 3/5"') + '; progress = 0.6 } |',
        f('  ConvertTo-Json'),
        k('Invoke-RestMethod') + f(' -Uri ') + s('"http://127.0.0.1:9840/v1/island/push"') + f(' -Method ') + 'Post `',
        f('  -Body ') + k('$card') + f(' -ContentType ') + s('"application/json; charset=utf-8"'),
        '',
        c('# 收回'),
        k('Invoke-RestMethod') + f(' -Uri ') + s('"http://127.0.0.1:9840/v1/island/push/build"') + f(' -Method ') + 'Delete'
      ],
      steps: [[0, 1, 2, 3, 4], [6, 7]]
    },
    wsl: {
      note: 'WSL2 · scripts/qisland-push 从标准输入读 JSON',
      lines: [
        c('# 镜像网络：直接打到 127.0.0.1:9840'),
        c('# NAT（WSL 默认）：自动改写到 Qisland 收件箱目录，慢一两拍'),
        k('echo') + ' ' + s('\'{"id":"build","title":"构建中","body":"step 3/5","progress":0.6}\'') + ' \\',
        '  | ' + k('bash') + ' scripts/qisland-push'
      ],
      steps: [[2, 3]]
    }
  };

  var codeBody = $('#codeBody');
  var codeNote = $('#codeNote');
  var runState = $('#runState');
  var copyBtn = $('#copyBtn');
  var runBtn = $('#runBtn');
  var simPill = $('#simPill');
  var simT = $('#simT');
  var simS = $('#simS');
  var simP = $('#simP');
  var simProg = $('#simProg');
  var islPushText = $('#islPushText');
  var islPushBar = $('#islPushBar');
  var lang = 'curl';
  var running = false;
  var copyTimer = 0;

  function renderCode() {
    if (!codeBody) return;
    codeBody.innerHTML = CODE[lang].lines.map(function (l) {
      return '<span class="ln">' + (l || ' ') + '</span>';
    }).join('');
    codeBody.setAttribute('aria-labelledby', 'tab-' + lang);
    if (codeNote) codeNote.textContent = CODE[lang].note;
  }

  function fallbackCopy(text) {
    try {
      var ta = document.createElement('textarea');
      ta.value = text;
      ta.setAttribute('readonly', '');
      ta.style.position = 'fixed';
      ta.style.left = '-9999px';
      ta.style.opacity = '0';
      document.body.appendChild(ta);
      ta.select();
      var ok = document.execCommand && document.execCommand('copy');
      document.body.removeChild(ta);
      return !!ok;
    } catch (_) {
      return false;
    }
  }

  if (codeBody) {
    renderCode();
    bindTablist('.code-bar [role="tablist"]', function (tab) {
      lang = tab.dataset.lang;
      renderCode();
    });

    copyBtn.addEventListener('click', function () {
      var txt = (codeBody.textContent || codeBody.innerText || '').replace(/\u00a0/g, ' ');
      var showCopied = function () {
        clearTimeout(copyTimer);
        copyBtn.textContent = '已复制 ✓';
        copyTimer = setTimeout(function () { copyBtn.textContent = '复制'; }, 2000);
      };
      if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(txt).then(showCopied, function () {
          fallbackCopy(txt);
          showCopied();
        });
      } else {
        fallbackCopy(txt);
        showCopied();
      }
    });

    runBtn.addEventListener('click', function () {
      if (running) return;
      running = true;
      runBtn.disabled = true;
      var lines = $$('.ln', codeBody);
      var hl = function (idx) {
        lines.forEach(function (l, i) { l.classList.toggle('hl', idx.indexOf(i) !== -1); });
      };
      var upd = function (title, sub, pct) {
        if (simPill) simPill.dataset.active = 'true';
        if (simT) simT.textContent = title;
        if (simS) simS.textContent = sub;
        if (simP) simP.textContent = pct + '%';
        if (simProg) simProg.style.setProperty('--sp', String(pct / 100));
        if (islPushText) islPushText.textContent = title + ' · ' + sub;
        if (islPushBar) islPushBar.style.setProperty('--p', String(pct / 100));
        runBtn.textContent = '模拟中 ' + pct + '%';
      };
      var stepMs = reduce ? 160 : 560;
      var wait = function (ms) {
        return new Promise(function (r) { setTimeout(r, reduce ? Math.min(ms, 220) : ms); });
      };

      runState.classList.add('on');
      hl(CODE[lang].steps[0]);
      runState.textContent = 'POST /v1/island/push · 200';
      flashIsland('push', 0);

      upd('构建中', 'step 1/5', 20);
      wait(stepMs).then(function () {
        upd('构建中', 'step 2/5', 40);
        return wait(stepMs);
      }).then(function () {
        upd('构建中', 'step 3/5', 60);
        return wait(stepMs);
      }).then(function () {
        upd('构建中', 'step 4/5', 80);
        return wait(stepMs);
      }).then(function () {
        if (lang === 'curl') {
          hl(CODE.curl.steps[1]);
          runState.textContent = '同 id 更新 · 200';
        }
        upd('构建完成', 'success', 100);
        return wait(1100);
      }).then(function () {
        var steps = CODE[lang].steps;
        var last = steps.length > 1 ? steps[steps.length - 1] : [];
        hl(last);
        runState.textContent = last.length ? 'DELETE /v1/island/push/build · 200' : '卡片到期自动收回';
        runBtn.textContent = '收回中…';
        if (simPill) simPill.dataset.active = 'false';
        flashTimer = 0;
        setIslandState(currentSectionState);
        return wait(900);
      }).then(function () {
        hl([]);
        runState.textContent = '就绪';
        runState.classList.remove('on');
        running = false;
        runBtn.disabled = false;
        runBtn.textContent = '重新模拟';
      });
    });
  }
})();

/* 宣传片：先读取 manifest 确认视频已就绪，再确认视频存在并加载封面，否则不请求媒体、保持「制作中」 */
(function () {
  var stage = document.getElementById('filmStage');
  var video = document.getElementById('promoVideo');
  var fallback = document.getElementById('filmFallback');
  if (!stage || !video || !fallback) return;

  var settled = false;
  function showPlayer() {
    if (settled) return;
    settled = true;
    stage.classList.add('is-ready');
    video.removeAttribute('aria-hidden');
    fallback.setAttribute('aria-hidden', 'true');
  }
  function keepFallback() {
    settled = false;
    stage.classList.remove('is-ready');
    video.setAttribute('aria-hidden', 'true');
    fallback.removeAttribute('aria-hidden');
  }

  keepFallback();
  if (typeof window.fetch !== 'function') return;

  var defaultSrc = video.getAttribute('data-src') || 'assets/media/qisland-4.1.mp4';
  var defaultPoster = video.getAttribute('data-poster') || 'assets/media/qisland-4.1-poster.jpg';

  function attachAndShow(videoSrc, posterSrc) {
    if (posterSrc) video.setAttribute('poster', posterSrc);
    var source = video.querySelector('source');
    if (!source) {
      source = document.createElement('source');
      source.type = 'video/mp4';
      video.appendChild(source);
    }
    source.src = videoSrc;
    source.addEventListener('error', keepFallback);
    video.addEventListener('error', keepFallback);
    video.load();
    showPlayer();
  }

  fetch('assets/media/manifest.json', { cache: 'no-cache' })
    .then(function (r) { return r.ok ? r.json() : null; })
    .then(function (meta) {
      if (!meta || !meta.ready) return;
      var videoSrc = meta.video || defaultSrc;
      var posterSrc = meta.poster || defaultPoster;
      return fetch(videoSrc, { method: 'HEAD' }).then(function (vr) {
        if (!vr.ok) return;
        if (!posterSrc) {
          attachAndShow(videoSrc, '');
          return;
        }
        var img = new Image();
        img.onload = function () { attachAndShow(videoSrc, posterSrc); };
        img.onerror = keepFallback;
        img.src = posterSrc;
      });
    })
    .catch(keepFallback);
})();
