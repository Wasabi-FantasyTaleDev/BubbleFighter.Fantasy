import type { Tutorial } from '../types'
import { prompt, reply } from './install-common'

// Install Bubble Fighter Fantasy on Windows by DeepSeek Harness. Its interface is Simplified Chinese
// or English, so Chinese pages show the Chinese interface and the others the English one; captions,
// the prompt and the agent's reply follow the page.

/** Names an interface label the way the reader sees it on screen. */
const ui = (en: string, zh: string) => ({ en, zh })

const add = ui('Add workspace...', '添加工作区...')
const write = ui('Workspace Write', '工作区内修改')
const full = ui('Full access', '完全权限')
const risk = ui('I understand the risks and want to continue', '我已了解风险，并愿意继续')
const download = ui('Download for Windows', '下载 Windows 版')

const tutorial: Tutorial = {
  notice: {
    en: { title: 'Before you start', ok: 'OK', body: [
      'DeepSeek Harness uses paid DeepSeek models: every task costs tokens. An install like this one costs about US$2-5.',
      'If your account has no balance left, top it up first. Without credit the agent cannot run.',
    ] },
    'zh-CN': { title: '开始之前', ok: '我知道了', body: [
      'DeepSeek Harness 使用按量计费的 DeepSeek 模型：每个任务都会消耗 Token。像这样的安装任务大约花费 5–10 元人民币。',
      '如果账户余额不足，请先充值，否则智能体无法运行。',
    ] },
    'zh-TW': { title: '開始之前', ok: '我知道了', body: [
      'DeepSeek Harness 使用按量計費的 DeepSeek 模型：每個任務都會消耗 Token。像這樣的安裝任務大約花費 2–5 美元。',
      '如果帳戶餘額不足，請先儲值，否則 AI 代理無法執行。',
    ] },
    ko: { title: '시작하기 전에', ok: '확인', body: [
      'DeepSeek Harness는 유료 DeepSeek 모델을 사용합니다. 모든 작업에 토큰이 들며, 이런 설치 작업은 약 2~5달러가 듭니다.',
      '계정 잔액이 없다면 먼저 충전하세요. 잔액이 없으면 에이전트가 실행되지 않습니다.',
    ] },
  },
  prompt: {
    agent: 'DeepSeek Harness',
    step: 4,
    title: { en: 'The install prompt', 'zh-CN': '安装提示词', 'zh-TW': '安裝提示詞', ko: '설치 프롬프트' },
    note: {
      en: 'Copy it into DeepSeek Harness in step 5. It works with any agent that can run commands on Windows.',
      'zh-CN': '在第 5 步复制到 DeepSeek Harness。任何能在 Windows 上运行命令的智能体都可以使用。',
      'zh-TW': '在第 5 步複製到 DeepSeek Harness。任何能在 Windows 上執行命令的 AI 代理都可以使用。',
      ko: '5단계에서 DeepSeek Harness에 붙여 넣으세요. Windows에서 명령을 실행할 수 있는 다른 에이전트에서도 쓸 수 있습니다.',
    },
    text: prompt,
  },
  steps: [
    {
      id: 'download',
      scene: 'browser',
      props: { product: 'dsh' },
      title: { en: 'Search for DeepSeek Harness and download it', 'zh-CN': '搜索 DeepSeek Harness 并下载', 'zh-TW': '搜尋 DeepSeek Harness 並下載', ko: 'DeepSeek Harness 검색 및 다운로드' },
      duration: 21500,
      cues: {
        open: 150, boxClick: 1500, typeStart: 1750, suggest: 1900, typeEnd: 3500, search: 4200, results: 4450,
        linkClick: 6000, site: 6300, dlClick: 9800, dlPanel: 10050, keep: 10250, done: 13250,
        explorer: 14500, tooltip: 15900, run: 17400, install: 17800, installed: 20100,
      },
      cursor: [
        { at: 600, x: 760, y: 560 },
        { at: 1500, target: 'search-box', dx: -180, dy: -4, click: true, move: 800 },
        { at: 4200, target: 'search-go', click: true, move: 650 },
        { at: 6000, target: 'result-link', dx: -60, click: true, move: 900 },
        { at: 9800, target: 'site-download', dx: -20, click: true, move: 1100 },
        { at: 12800, x: 980, y: 300, move: 900 },
        { at: 14300, target: 'dl-folder', click: true, move: 900 },
        { at: 15900, target: 'exp-file', dx: -120, move: 1000 },
        { at: 17400, target: 'exp-file', dx: -120, click: true, double: true, move: 200 },
        { at: 18600, x: 900, y: 600, move: 900 },
      ],
      camera: [
        { at: 0, x: 640, y: 380, z: 1, mz: 1.5 },
        { at: 1200, target: 'search-box', z: 1.12, mz: 2.1 },
        { at: 4500, target: 'result-link', y: 330, z: 1, mz: 1.7 },
        { at: 6300, x: 640, y: 360, z: 1, mz: 1.5 },
        { at: 9200, target: 'site-download', z: 1.08, mz: 2 },
        { at: 10100, x: 1080, y: 230, z: 1.3, mz: 2.3 },
        { at: 14500, target: 'exp-file', z: 1.15, mz: 2.1 },
        { at: 17800, x: 640, y: 380, z: 1.05, mz: 1.8 },
      ],
      captions: [
        { at: 0, text: { en: 'Open a browser and go to Google.', 'zh-CN': '打开浏览器，进入百度。', 'zh-TW': '開啟瀏覽器，進入 Google。', ko: '브라우저를 열고 Google에 들어갑니다.' } },
        { at: 1500, text: { en: 'Search for DeepSeek Harness.', 'zh-CN': '搜索 DeepSeek Harness。', 'zh-TW': '搜尋 DeepSeek Harness。', ko: 'DeepSeek Harness를 검색합니다.' } },
        { at: 4450, text: { en: 'Open the official DeepSeek Harness page on deepseek.com.', 'zh-CN': '打开 deepseek.com 上的 DeepSeek Harness 官方页面。', 'zh-TW': '開啟 deepseek.com 上的 DeepSeek Harness 官方頁面。', ko: 'deepseek.com의 DeepSeek Harness 공식 페이지를 엽니다.' } },
        { at: 8000, text: { en: `Click “${download.en}”.`, 'zh-CN': `点击“${download.zh}”。`, 'zh-TW': `點選「${download.zh}」（下載 Windows 版）。`, ko: `“${download.en}”를 클릭합니다.` } },
        { at: 10050, text: { en: 'Wait for the download to finish.', 'zh-CN': '等待下载完成。', 'zh-TW': '等待下載完成。', ko: '다운로드가 끝날 때까지 기다립니다.' } },
        { at: 13250, text: { en: 'Show it in its folder and double-click the installer.', 'zh-CN': '在文件夹中显示它，然后双击安装程序。', 'zh-TW': '在資料夾中顯示它，然後按兩下安裝程式。', ko: '폴더에서 파일을 열고 설치 프로그램을 두 번 클릭합니다.' } },
        { at: 17800, text: { en: 'Let the installer finish.', 'zh-CN': '等待安装完成。', 'zh-TW': '等待安裝完成。', ko: '설치가 끝날 때까지 기다립니다.' } },
      ],
    },
    {
      id: 'open',
      scene: 'dsh',
      title: { en: 'Open DeepSeek Harness', 'zh-CN': '打开 DeepSeek Harness', 'zh-TW': '開啟 DeepSeek Harness', ko: 'DeepSeek Harness 열기' },
      duration: 6500,
      props: { phase: 'open' },
      cues: { open: 150, loaded: 2600 },
      cursor: [
        { at: 900, x: 760, y: 640 },
        { at: 4200, x: 820, y: 560, move: 1400 },
      ],
      camera: [
        { at: 0, x: 640, y: 380, z: 1, mz: 1.5 },
        { at: 2900, x: 660, y: 390, z: 1.06, mz: 1.9, dur: 1400 },
      ],
      captions: [
        { at: 0, text: { en: 'Start DeepSeek Harness. It loads its plugins first.', 'zh-CN': '启动 DeepSeek Harness，它会先加载插件。', 'zh-TW': '啟動 DeepSeek Harness，它會先載入模組。', ko: 'DeepSeek Harness를 시작합니다. 먼저 플러그인을 불러옵니다.' } },
        { at: 2800, text: { en: 'Then the main page opens with the chat box. Its interface is in Chinese or English.', 'zh-CN': '加载完成后，主页面和对话框会出现。', 'zh-TW': '載入完成後會出現主頁面和對話框。它的介面只有簡體中文和英文。', ko: '그다음 채팅 상자가 있는 메인 화면이 열립니다. 인터페이스는 중국어 또는 영어입니다.' } },
      ],
    },
    {
      id: 'workspace',
      scene: 'dsh',
      title: { en: 'Select a folder as the workspace', 'zh-CN': '选择一个文件夹作为工作区', 'zh-TW': '選擇一個資料夾作為工作區', ko: '폴더를 작업 공간으로 선택' },
      duration: 11000,
      props: { phase: 'workspace' },
      cues: { wsMenu: 1450, wsHover: 2600, dialog: 3000, newPress: 4150, newFolder: 4300, nameStart: 4550, nameEnd: 5900, okPress: 7050, dialogClose: 7200 },
      cursor: [
        { at: 300, x: 700, y: 640 },
        { at: 1400, target: 'ds-ws', dx: -20, click: true, move: 900 },
        { at: 2800, target: 'ds-ws-add', dx: -30, click: true, move: 800 },
        { at: 4200, target: 'fd-new', click: true, move: 1000 },
        { at: 6200, target: 'fd-newrow', dx: -150, move: 700 },
        { at: 7100, target: 'fd-ok', click: true, move: 800 },
        { at: 8300, target: 'ds-ws', dx: 50, dy: 30, move: 900 },
      ],
      camera: [
        { at: 0, x: 640, y: 400, z: 1, mz: 1.6 },
        { at: 1000, target: 'ds-ws', y: 420, z: 1.15, mz: 2.2 },
        { at: 3000, x: 610, y: 300, z: 1, mz: 1.5 },
        { at: 3800, target: 'fd-new', y: 200, z: 1.1, mz: 2.2 },
        { at: 6600, target: 'fd-ok', y: 420, z: 1.05, mz: 2 },
        { at: 7400, target: 'ds-ws', y: 410, z: 1.15, mz: 2.2 },
      ],
      captions: [
        { at: 0, text: { en: 'Open the workspace menu above the chat box.', 'zh-CN': '打开对话框上方的工作区菜单。', 'zh-TW': '開啟對話框上方的工作區選單。', ko: '채팅 상자 위의 작업 공간 메뉴를 엽니다.' } },
        { at: 2600, text: { en: `Choose “${add.en}”.`, 'zh-CN': `选择“${add.zh}”。`, 'zh-TW': `選擇「${add.zh}」（新增工作區）。`, ko: `“${add.en}”(작업 공간 추가)를 선택합니다.` } },
        { at: 4100, text: { en: 'Create a new folder, for example BubbleFighterFantasy, and select it.', 'zh-CN': '新建一个文件夹，例如 BubbleFighterFantasy，然后选择它。', 'zh-TW': '新增一個資料夾，例如 BubbleFighterFantasy，然後選擇它。', ko: 'BubbleFighterFantasy 같은 새 폴더를 만들고 선택합니다.' } },
        { at: 7400, text: { en: 'The agent works in this folder. The server itself goes to C:\\BubbleFighterFantasy, a short path Windows accepts.', 'zh-CN': '智能体在这个文件夹里工作，服务器本身会安装到 C:\\BubbleFighterFantasy（Windows 能接受的短路径）。', 'zh-TW': 'AI 代理在這個資料夾裡工作，伺服器本身會安裝到 C:\\BubbleFighterFantasy（Windows 能接受的短路徑）。', ko: '에이전트는 이 폴더에서 작업합니다. 서버 자체는 Windows가 허용하는 짧은 경로인 C:\\BubbleFighterFantasy에 설치됩니다.' } },
      ],
    },
    {
      id: 'permission',
      scene: 'dsh',
      title: { en: 'Change the permission to full access', 'zh-CN': '把权限改为完全权限', 'zh-TW': '把權限改為完全權限', ko: '권한을 전체 접근으로 변경' },
      duration: 9500,
      props: { phase: 'permission' },
      cues: { permMenu: 1450, permHover: 2500, permDialog: 3000, check: 4800, allowPress: 6100, allow: 6250 },
      cursor: [
        { at: 300, x: 700, y: 640 },
        { at: 1400, target: 'ds-perm', dx: -10, click: true, move: 900 },
        { at: 2950, target: 'ds-perm-full', dx: -20, click: true, move: 800 },
        { at: 4750, target: 'ds-check', click: true, move: 1000 },
        { at: 6150, target: 'ds-enable', click: true, move: 900 },
        { at: 7400, target: 'ds-perm', dx: 60, dy: 40, move: 900 },
      ],
      camera: [
        { at: 0, x: 640, y: 420, z: 1, mz: 1.7 },
        { at: 900, target: 'ds-perm', y: 400, z: 1.15, mz: 2.2 },
        { at: 3100, x: 640, y: 380, z: 1.05, mz: 1.7 },
        { at: 6600, target: 'ds-perm', y: 430, z: 1.15, mz: 2.2 },
      ],
      captions: [
        { at: 0, text: { en: `Open the permission menu (${write.en}) in the chat box.`, 'zh-CN': `打开对话框里的权限菜单（${write.zh}）。`, 'zh-TW': `開啟對話框裡的權限選單（${write.zh}）。`, ko: `채팅 상자의 권한 메뉴(${write.en})를 엽니다.` } },
        { at: 2400, text: { en: `Choose ${full.en}, the “danger” level.`, 'zh-CN': `选择“${full.zh}”，也就是危险级别。`, 'zh-TW': `選擇「${full.zh}」，也就是危險級別。`, ko: `위험 단계인 ${full.en}(전체 접근)를 선택합니다.` } },
        { at: 3100, text: { en: 'With full access the agent runs commands and changes files without asking each time. Use it only in the folder you just made.', 'zh-CN': '启用后，智能体运行命令和修改文件时不再每次询问。只在刚才新建的文件夹里使用。', 'zh-TW': '啟用後，AI 代理執行命令和修改檔案時不再每次詢問。只在剛才新增的資料夾裡使用。', ko: '전체 접근을 켜면 에이전트가 매번 묻지 않고 명령을 실행하고 파일을 바꿉니다. 방금 만든 폴더에서만 사용하세요.' } },
        { at: 4800, text: { en: `Tick “${risk.en}” and enable it.`, 'zh-CN': `勾选“${risk.zh}”，然后启用。`, 'zh-TW': `勾選「${risk.zh}」，然後啟用。`, ko: `“${risk.en}”(위험을 이해함)에 체크하고 켭니다.` } },
        { at: 6600, text: { en: `The chat box now shows ${full.en}.`, 'zh-CN': `对话框现在显示“${full.zh}”。`, 'zh-TW': `對話框現在顯示「${full.zh}」。`, ko: `이제 채팅 상자에 ${full.en}가 표시됩니다.` } },
      ],
    },
    {
      id: 'install',
      scene: 'dsh',
      title: { en: 'Send the prompt; DeepSeek Harness installs the server', 'zh-CN': '发送提示词，DeepSeek Harness 为你安装服务器', 'zh-TW': '傳送提示詞，DeepSeek Harness 為你安裝伺服器', ko: '프롬프트를 보내면 DeepSeek Harness가 서버를 설치' },
      duration: 33500,
      props: {
        phase: 'chat',
        prompt,
        reply: reply(12600),
        taskTitle: { en: 'Install Bubble Fighter Fantasy', 'zh-CN': '安装 Bubble Fighter Fantasy', 'zh-TW': '安裝 Bubble Fighter Fantasy', ko: 'Bubble Fighter Fantasy 설치' },
        thought: {
          en: 'The user wants the Bubble Fighter Fantasy server installed in this folder. Check the processor first, then the package…',
          'zh-CN': '用户要在这个文件夹安装 Bubble Fighter Fantasy 服务器。先检测处理器，再准备安装包…',
          'zh-TW': '使用者要在這個資料夾安裝 Bubble Fighter Fantasy 伺服器。先偵測處理器，再準備安裝套件…',
          ko: '사용자가 이 폴더에 Bubble Fighter Fantasy 서버 설치를 원합니다. 먼저 프로세서를 확인하고 패키지를 준비합니다…',
        },
      },
      cues: { focus: 1000, typeStart: 1200, typeEnd: 8200, sendPress: 9100, send: 9250, think: 11000, respond: 12600, done: 31400 },
      cursor: [
        { at: 300, x: 760, y: 620 },
        { at: 1000, target: 'composer', dx: -200, dy: -20, click: true, move: 700 },
        { at: 2400, x: 1000, y: 690, move: 900 },
        { at: 9100, target: 'send', click: true, move: 900 },
        { at: 10200, x: 1110, y: 720, move: 900 },
        { at: 10600, x: 1110, y: 720, hide: true },
      ],
      camera: [
        { at: 0, target: 'composer', y: 400, z: 1.1, mz: 2 },
        { at: 8500, target: 'send', y: 400, z: 1.1, mz: 2 },
        { at: 9400, x: 740, y: 330, z: 1.04, mz: 1.8 },
        { at: 13500, x: 740, y: 360, z: 1.04, mz: 1.8 },
      ],
      captions: [
        { at: 0, text: { en: 'Click the chat box and paste the install prompt (copy it below the player).', 'zh-CN': '点击对话框，粘贴安装提示词（在播放器下方复制）。', 'zh-TW': '點選對話框，貼上安裝提示詞（在播放器下方複製）。', ko: '채팅 상자를 클릭하고 설치 프롬프트를 붙여 넣습니다(플레이어 아래에서 복사).' } },
        { at: 8600, text: { en: 'Send it.', 'zh-CN': '发送。', 'zh-TW': '傳送。', ko: '보냅니다.' } },
        { at: 9300, text: { en: 'DeepSeek Harness starts working: it thinks, then runs the steps one by one.', 'zh-CN': 'DeepSeek Harness 开始工作：先思考，再逐步执行。', 'zh-TW': 'DeepSeek Harness 開始工作：先思考，再逐步執行。', ko: 'DeepSeek Harness가 작업을 시작합니다: 생각한 뒤 단계를 하나씩 실행합니다.' } },
        { at: 13600, text: { en: 'It reads the deployment guide, then runs the project\'s install script, which downloads and verifies the package and installs the runtime when it is missing.', 'zh-CN': '它先阅读部署指南，再运行项目自带的安装脚本：脚本会下载并校验安装包，缺少运行时时自动安装。', 'zh-TW': '它先閱讀部署指南，再執行專案內建的安裝腳本：腳本會下載並校驗安裝套件，缺少執行階段時自動安裝。', ko: '배포 가이드를 읽은 뒤 프로젝트의 설치 스크립트를 실행합니다. 스크립트가 패키지를 내려받아 검증하고, 런타임이 없으면 설치합니다.' } },
        { at: 22800, text: { en: 'The script starts the server in its own window and waits until http://127.0.0.1:7300/health answers ok.', 'zh-CN': '脚本在单独的窗口中启动服务器，并等待 http://127.0.0.1:7300/health 返回 ok。', 'zh-TW': '腳本在獨立的視窗中啟動伺服器，並等待 http://127.0.0.1:7300/health 回傳 ok。', ko: '스크립트가 서버를 별도 창에서 시작하고 http://127.0.0.1:7300/health가 ok를 반환할 때까지 기다립니다.' } },
        { at: 26700, text: { en: 'It ends with a Markdown summary. Open http://127.0.0.1:7300/ to finish the setup assistant.', 'zh-CN': '最后用 Markdown 总结。打开 http://127.0.0.1:7300/ 完成设置向导。', 'zh-TW': '最後用 Markdown 總結。開啟 http://127.0.0.1:7300/ 完成設定精靈。', ko: '마지막으로 Markdown 요약을 보여 줍니다. http://127.0.0.1:7300/을 열어 설정 도우미를 마치세요.' } },
      ],
    },
  ],
}

export default tutorial
