/* ===================================================================
 * JS 渲染引擎 — AIStreamResultDialog（第十七版 — 两步 API + 思考过程实时渲染）
 * 
 * 依赖：WebView2（Chromium 内核），支持 ES6+ / fetch / ReadableStream
 * BASE_URL、AUTH_TOKEN、PROMPT_MESSAGE 由 C# 注入
 * 页面加载后自动调用 callChatApi()
 * 
 * v17 关键变化：
 * - 支持 reasoning_content（AI思考过程）实时渲染到折叠区域
 * - content（正式回答）渲染到主内容区
 * - 思考阶段显示"AI 正在思考..."状态
 * - 思考结束后自动折叠思考区
 * =================================================================== */

let fullText = '';
let thinkText = '';
let isComplete = false;
let renderScheduled = false;
let thinkRenderScheduled = false;
let hasContentStarted = false;
let sseData = '';

// ── 两步 Chat API 调用 ──────────────────────────────

async function callChatApi() {
    try {
        // Step 1: GET /chat/api/open → 获取 chat_id
        const openResponse = await fetch(BASE_URL + '/chat/api/open', {
            method: 'GET',
            headers: {
                'Authorization': AUTH_TOKEN,
            },
        });

        if (!openResponse.ok) {
            showError('获取 chat_id 失败: HTTP ' + openResponse.status);
            return;
        }

        const openData = await openResponse.json();
        if (openData.code !== 200 || !openData.data) {
            showError('获取 chat_id 失败: ' + (openData.message || '响应数据异常'));
            return;
        }

        const chatId = openData.data;

        // 更新状态：正在请求 AI
        updateStatus('AI 正在思考...', true);

        // Step 2: POST /chat/api/chat_message/{chatId} → SSE 流式响应
        const chatResponse = await fetch(BASE_URL + '/chat/api/chat_message/' + chatId, {
            method: 'POST',
            headers: {
                'Authorization': AUTH_TOKEN,
                'Content-Type': 'application/json',
            },
            body: JSON.stringify({
                message: PROMPT_MESSAGE,
                stream: true,
                re_chat: false,
            }),
        });

        if (!chatResponse.ok) {
            showError('AI 请求失败: HTTP ' + chatResponse.status);
            return;
        }

        // 逐字节流式读取 + SSE 帧解析
        const reader = chatResponse.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';
        sseData = '';

        while (true) {
            const { done, value } = await reader.read();
            if (done) {
                // 处理缓冲区中残余的未完成行
                if (buffer.trim().length > 0) {
                    processSseLine(buffer.trim());
                }
                // 处理最后一段 data
                if (sseData.length > 0) {
                    processSseEvent(sseData);
                }
                break;
            }

            buffer += decoder.decode(value, { stream: true });

            // 按行分割，处理完整的行
            const lines = buffer.split('\n');
            buffer = lines.pop() || ''; // 最后一段可能不完整

            for (const line of lines) {
                processSseLine(line);
            }
        }

        // 流正常结束
        finishStream();

    } catch (err) {
        showError('AI 请求异常: ' + (err.message || '未知错误'));
    }
}

// ── SSE 行处理 ────────────────────────────

function processSseLine(line) {
    const trimmed = line.trim();

    // 空行 = SSE 事件分隔符，派发当前累积的 data
    if (trimmed === '') {
        if (sseData.length > 0) {
            processSseEvent(sseData);
            sseData = '';
        }
        return;
    }

    // 注释行忽略
    if (trimmed.startsWith(':')) return;

    // 解析 field: value
    const colonIdx = trimmed.indexOf(':');
    if (colonIdx === -1) return;

    const field = trimmed.substring(0, colonIdx);
    let value = trimmed.substring(colonIdx + 1);
    // SSE 规范：冒号后第一个空格可选
    if (value.length > 0 && value[0] === ' ') {
        value = value.substring(1);
    }

    if (field === 'data') {
        sseData += (sseData.length > 0 ? '\n' : '') + value;
    }
}

// ── SSE 事件处理（解析 JSON，提取 content + reasoning_content）───

function processSseEvent(data) {
    try {
        const parsed = JSON.parse(data);

        // 提取 reasoning_content（AI 思考过程）
        const reasoning = parsed.reasoning_content;
        if (reasoning) {
            thinkText += reasoning;
            scheduleThinkRender();
        }

        // 提取 content（正式回答）
        const content = parsed.content;
        if (content) {
            if (!hasContentStarted) {
                hasContentStarted = true;
                // content 开始输出，更新状态
                updateStatus('AI 正在回答...', true);
                // 如果有思考内容，折叠思考区
                if (thinkText.length > 0) {
                    collapseThinking();
                }
            }
            fullText += content;
            scheduleRender();
        }

        // 检查是否结束
        if (parsed.is_end === true) {
            isComplete = true;
        }
    } catch (e) {
        showError('AI 响应数据解析失败: ' + e.message + '\n原始报文: ' + data);
    }
}

// ── 状态更新 ────────────────────────────────

function updateStatus(msg, isThinking) {
    // 通知 C# 更新状态栏
    window.chrome.webview.postMessage(
        JSON.stringify({ type: 'status', message: msg, thinking: isThinking }));
}

// ── 折叠思考区 ────────────────────────────────

function collapseThinking() {
    const thinkArea = document.getElementById('thinking-area');
    if (thinkArea) {
        thinkArea.classList.add('collapsed');
        const header = document.getElementById('thinking-header');
        if (header) header.textContent = '💭 AI 思考过程（点击展开/折叠）';
    }
}

// ── 渲染调度（requestAnimationFrame，零延迟）────────

function scheduleRender() {
    if (!renderScheduled) {
        renderScheduled = true;
        requestAnimationFrame(doRender);
    }
}

function scheduleThinkRender() {
    if (!thinkRenderScheduled) {
        thinkRenderScheduled = true;
        requestAnimationFrame(doThinkRender);
    }
}

function finishStream() {
    isComplete = true;
    renderScheduled = false;
    thinkRenderScheduled = false;
    doRender();
    doThinkRender();
    // 通知 C# 请求完成
    window.chrome.webview.postMessage(
        JSON.stringify({ type: 'complete', text: fullText }));
}

function showError(msg) {
    const el = document.getElementById('content');
    if (el) {
        const htmlMsg = msg.replace(/\n/g, '<br>').replace(/ /g, '&nbsp;');
        el.innerHTML = '<div style="text-align:center;padding:40px 20px;color:#EF4444;">' +
            '<div style="font-size:48px;margin-bottom:16px;">&#x26A0;</div>' +
            '<div style="font-size:14px;font-weight:500;margin-bottom:8px;">AI 请求失败</div>' +
            '<div style="font-size:12px;color:#94A3B8;text-align:left;max-width:600px;margin:0 auto;word-break:break-all;font-family:Consolas,monospace;">' + htmlMsg + '</div></div>';
    }
    // 通知 C# 请求出错
    window.chrome.webview.postMessage(
        JSON.stringify({ type: 'error', message: msg }));
}

// ── 渲染引擎 ──────────────────────────────

function doThinkRender() {
    thinkRenderScheduled = false;
    const thinkArea = document.getElementById('thinking-area');
    if (!thinkArea || !thinkText) return;

    // 显示思考区（首次有内容时）
    if (thinkArea.style.display === 'none' || !thinkArea.style.display) {
        thinkArea.style.display = 'block';
        const header = document.getElementById('thinking-header');
        if (header) header.textContent = '💭 AI 正在思考...';
    }

    const thinkBody = document.getElementById('thinking-body');
    if (thinkBody) {
        thinkBody.innerHTML = escHtml(thinkText).replace(/\n/g, '<br>');
    }
}

function doRender() {
    renderScheduled = false;
    if (!fullText) return;
    document.getElementById('content').innerHTML = md(fullText);
    window.scrollTo(0, document.body.scrollHeight);
}

// ── 内联 Markdown 渲染器（使用 marked 库）────────────

function md(text) {
    if (!text) return '';
    if (typeof marked === 'undefined') {
        // 降级：如果 marked 库未加载，做最基本的转义
        return escHtml(text).replace(/\n/g, '<br>');
    }
    try {
        // marked 配置：GFM + 换行转 <br>
        marked.setOptions({
            gfm: true,
            breaks: true,
            silent: true
        });
        // 流式渲染时，未闭合的代码块（奇数个 ```）需要补一个闭合标记
        // 否则 marked 会把后续所有文本都当代码块处理
        var renderText = text;
        var fenceCount = (renderText.match(/```/g) || []).length;
        if (fenceCount % 2 !== 0) {
            // 奇数个围栏 → 补一个闭合标记
            renderText = renderText + '\n```';
        }
        return marked.parse(renderText);
    } catch (e) {
        // 降级：至少把文本显示出来
        return escHtml(text).replace(/\n/g, '<br>');
    }
}

function escHtml(s) {
    return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

// ── 自动启动 ────────────────────────────────
callChatApi();