/**
 * 附件上传的启动前承载（D07；D13 增加归属品牌与"不回退裸 /api"的运行时规则）。
 *
 * 设计要点（方案 2.0 第 4.3 节）：
 * - Harness 的 FileUploadRuntime 会在初始化时捕获当时的 `window.fetch`；默认 Blob
 *   上传走 Worker 内 XHR，主 window 的 fetch 改写拦不到。因此我们在**任何 boot
 *   entry 之前**（parse-time head script）安装 `__DSH_FILE_UPLOAD__`，让上传改走
 *   当前页面的 fetch，从而经过全家桶 remote 的 `/remote` 门控代理与设备头注入。
 * - 调用时才读取 `globalThis.fetch`（不提前捕获裸 fetch）：remote 的改写是后装的，
 *   提前捕获会绕过设备头注入。
 * - 只传原始同源 `/api/...` 路径，**不预拼 /remote**：remote 的改写只对 `/api/`
 *   前缀且同源生效；预拼会导致既不注入设备头、又被重复改写。
 * - 用 `new Headers(init?.headers)` 统一头部载体：remote 的设备头注入对 `Headers`
 *   实例走 `set()`，对普通对象走拷贝；统一成 Headers 行为最可预期。
 * - 保留 `signal` 原样传给 fetch，取消语义由调用方掌控。
 * - 不解析响应信封：HTTP 200 的业务失败（`{ok:false,error}`）交给上游 envelope
 *   解析，hook 不吞错、不改写状态码。
 *
 * D13 新增的两条（都是"诚实"要求，不是新功能）：
 *
 * 1. **归属品牌**：座位对象带 `brand`。client 半区据此判断"这个 hook 是我们装的"
 *    （可以撤销），从而**绝不**删除别人装的未知 hook。
 * 2. **不回退裸 /api**：非 loopback 页面上若 remote 的启动前 seat 不在（远程被禁用/
 *    降级），就地以 `capability-disabled` 拒绝，而不是把请求打到裸 `/api`——那正是
 *    方案禁止的静默回退（既不注入设备头，也绕过门控通道）。
 */

import { CARRIER_BRAND, STATUS_GLOBAL, UPLOAD_HOOK_GLOBAL } from '../shared/capabilities.js'

export { CARRIER_BRAND, UPLOAD_HOOK_GLOBAL }

/** 承载脚本的版本（诊断用；与品牌一起写进状态位）。 */
export const UPLOAD_HOOK_VERSION = 2

/** remote 启动前 seat 的全局名（只读判断，不触碰它的任何方法）。 */
export const REMOTE_SEAT_GLOBAL = '__DSH_REMOTE_CHANNEL_BOOT__'

/**
 * 生成启动前注入脚本。
 *
 * 脚本必须自包含（注入到 `<head>` 内联执行），不能用模块导入或 TypeScript 语法。
 * 它**不覆盖**已存在的同名全局：遇到未知 hook 时保留原值并把冲突写入自己的诊断位，
 * 由宿主侧读取后报告 capability conflict。
 *
 * **形状必须是 fetch 形状的载体（`{ fetch }`），不是裸函数。** 固定版本消费者
 * `@deepseek-ai/dsh-client-file-upload@0.1.5-rc.2` 读的是 `hook.fetch`：
 *
 * ```js
 * // lib/client.js:161
 * this.transport = hook === void 0 ? workerTransport() : customTransport(hook.fetch);
 * ```
 *
 * 该包 README 的原话是 "supply a Fetch-shaped carrier before Cordis boots"。D09 实测：
 * 装成裸函数时 `hook.fetch` 为 undefined → `customTransport(undefined)` → 文本附件上传
 * 抛 TypeError，composer 芯片停在"上传失败，点击重试"，`uploadsPending` 让发送永久禁用。
 * （D07 曾把 `typeof hook === 'function'` 写成判据，所以那道门测不出这个缺陷；已同步修正。）
 */
export function buildUploadHookScript(): string {
  return `(function(){
var G=${JSON.stringify(UPLOAD_HOOK_GLOBAL)};
var R=${JSON.stringify(REMOTE_SEAT_GLOBAL)};
var B=${JSON.stringify(CARRIER_BRAND)};
var V=${JSON.stringify(UPLOAD_HOOK_VERSION)};
var w=globalThis;
function st(){try{return (w.__DSH_ATTACHMENTS_STATUS__=w.__DSH_ATTACHMENTS_STATUS__||{})}catch(e){return null}}
var seat=w[G];
if(seat!==undefined&&seat!==null){
  // 未知 hook：绝不覆盖，只登记冲突供宿主诊断（保留原功能）。
  var s0=st();
  if(s0)s0.uploadHook='conflict';
  return;
}
// loopback（本机桌面）由应用直接访问自己的 origin，remote 的 boot 脚本按设计跳过；
// 非 loopback 页面必须真的有 remote 的启动前 seat，否则我们不接管上传。
function loopback(){try{var h=w.location.hostname;return h==='localhost'||h==='::1'||h==='[::1]'||/^127(\\.\\d{1,3}){3}$/.test(h)}catch(e){return false}}
function hasSeat(){try{var v=w[R];return typeof v==='object'&&v!==null&&typeof v.restore==='function'}catch(e){return false}}
var missingUrl='attachment upload hook received no usable url';
function carrier(input,init){
  var s=st();
  // 拒绝静默回退到裸 /api：没有门控改写层时宁可不发请求，也不绕过通道。
  if(!loopback()&&!hasSeat()){
    if(s)s.uploadRoute='refused-no-remote-channel';
    var deny=new Error('remote-attachments: capability-disabled: 本页面未安装门控上传通道改写层，拒绝回退到同源裸 api 上传');
    deny.code='capability-disabled';
    return Promise.reject(deny);
  }
  if(s)s.uploadRoute=loopback()?'loopback-direct':'remote-rewrite';
  // 调用时读取当前 fetch：remote 的改写可能晚于本脚本安装。
  var f=w.fetch;
  if(typeof f!=='function')return Promise.reject(new Error('fetch is unavailable in this context'));
  var raw;
  if(typeof input==='string'||input instanceof URL){raw=input.toString()}
  else if(input&&typeof input.url==='string'){raw=input.url}
  else{return Promise.reject(new Error(missingUrl))}
  var url;
  try{url=new URL(raw,location.href)}catch(e){return Promise.reject(e)}
  var nextInit=init===undefined?{}:init;
  var headers;
  try{headers=new Headers(nextInit&&nextInit.headers)}
  catch(e){headers=new Headers()}
  var out={
    method:nextInit.method,
    headers:headers,
    body:nextInit.body,
    credentials:nextInit.credentials,
    cache:nextInit.cache,
    mode:nextInit.mode,
    redirect:nextInit.redirect,
    referrer:nextInit.referrer,
    referrerPolicy:nextInit.referrerPolicy,
    integrity:nextInit.integrity,
    keepalive:nextInit.keepalive
  };
  // 保留 signal：取消语义原样交给 fetch；未提供时删除键，避免显式 undefined。
  if(nextInit.signal!==undefined)out.signal=nextInit.signal;
  for(var k in out){if(out[k]===undefined)delete out[k]}
  var target=url.toString();
  try{(w.__DSH_ATTACHMENTS_STATUS__=w.__DSH_ATTACHMENTS_STATUS__||{}).uploadHook='installed'}catch(e){}
  // 返回原始 Response：HTTP 200 的业务失败由上游 envelope 解析。
  return f.call(w,target,out);
}
// 固定版本消费者读 hook.fetch（见上方契约说明），因此装成 fetch 形状而不是裸函数。
w[G]={fetch:carrier};
// 品牌：只有带这个值的座位才是本插件装的，client 半区据此决定"能否撤销"。
try{w[G].brand=B}catch(e){}
try{
  var s1=(w.__DSH_ATTACHMENTS_STATUS__=w.__DSH_ATTACHMENTS_STATUS__||{});
  if(s1.uploadHook!=='conflict'){
    s1.uploadHook='installed';
    s1.host={hookVersion:V,brand:B,carrierShape:'fetch'};
  }
}catch(e){}
})();`
}

/** 允许消费方声明的上传目标路径（仅用于诊断，不参与改写）。 */
export const UPLOAD_ENDPOINT = '/api/session/uploadFileBinary'

/**
 * 生成"本插件被显式禁用"的启动前标记脚本（D23）。
 *
 * 为什么需要一个标记：cordis 的浏览器半区加载器是 `loader.create({ name })`——
 * **不带行配置**（host 侧的 `config.enabled: false` 传不到页面里的 client 半区）。
 * 没有这个标记时，被禁用的插件在 loopback 页面上（那里本来就不需要上传承载）会把自己
 * 报成 `available`，并且桥照旧能把文件塞进草稿——即"禁用"在本地页面上是失效的。
 *
 * 标记只写诊断座位里的 `host.enabled=false`，**不**安装承载、**不**碰
 * `__DSH_FILE_UPLOAD__`、**不**碰 remote 的 seat：未知 hook 的保留规则不受影响。
 */
export function buildDisabledHookScript(): string {
  return `(function(){try{var w=globalThis;var s=(w[${JSON.stringify(STATUS_GLOBAL)}]=w[${JSON.stringify(STATUS_GLOBAL)}]||{});s.host={enabled:false,hookVersion:${JSON.stringify(UPLOAD_HOOK_VERSION)},brand:${JSON.stringify(CARRIER_BRAND)},carrierShape:'none'};}catch(e){}})();`
}

/**
 * 读取承载的安装状态（供宿主诊断与测试断言）。
 *
 * - `install`：座位空着，可以安装；
 * - `owned`：座位上已经是本插件装的承载（品牌匹配）——可以撤销；
 * - `conflict`：座位上另有其物（未知 hook）——保留原功能，绝不覆盖、绝不删除。
 *
 * @param existing 页面上已存在的 `__DSH_FILE_UPLOAD__` 值
 * @returns 安装状态判定
 */
export function classifyHookInstallation(existing: unknown): 'install' | 'conflict' | 'owned' {
  if (existing === undefined || existing === null) return 'install'
  const brand = (existing as { readonly brand?: unknown }).brand
  return brand === CARRIER_BRAND ? 'owned' : 'conflict'
}
