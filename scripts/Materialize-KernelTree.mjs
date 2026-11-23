/**
 * 把内核子树物化成「真文件」的发行树。
 *
 * pnpm 的 isolated 布局是一堆 junction：tar / robocopy 会跟着它递归（本地实测把
 * bsdtar 跑到 access violation），而 zip 解压时 Windows 又建不出符号链接。所以
 * 发行包必须是不含任何 reparse point 的真实目录树。
 *
 * 做法：
 *  - 目录：照建
 *  - 文件：优先 fs.linkSync（同卷硬链接，保住 pnpm 的去重，不额外占盘），失败再复制
 *  - 链接（含 junction）：解析真实路径后把内容搬过来，绝不保留链接
 *  - 环：用 realpath 调用栈判定；真出现环就记下来并以退出码 3 失败，
 *        绝不悄悄产出一棵残缺的树
 *
 * 用法：node Materialize-KernelTree.mjs <source> <dest> <includes> <excludes>
 *   includes / excludes 是相对 source 的 posix 路径，逗号分隔，可留空。
 */
import fs from 'node:fs'
import path from 'node:path'

const [source, dest, includesArg, excludesArg] = process.argv.slice(2)
if (!source || !dest) {
  console.error('usage: Materialize-KernelTree.mjs <source> <dest> <includes> <excludes>')
  process.exit(2)
}

const includes = (includesArg ?? '').split(',').map(s => s.trim()).filter(Boolean)
const excludes = (excludesArg ?? '').split(',').map(s => s.trim()).filter(Boolean)

const stats = { dirs: 0, files: 0, hardlinked: 0, copied: 0, links: 0, dangling: 0, cycles: 0, bytes: 0 }
const cyclePaths = []

const toPosix = p => p.split(path.sep).join('/')

/** excludes 里以 / 结尾表示整个目录；否则按前缀匹配 */
function isExcluded(rel) {
  for (const rule of excludes) {
    if (rel === rule) return true
    if (rel.startsWith(`${rule}/`)) return true
  }
  // 除根 node_modules 之外，任何层级的 node_modules 都不复制。
  // hoisted 布局下各 workspace 项目自己的 node_modules 只是指回根上的链接，
  // 复制它们会把同一批包重复搬进发行树（实测 34 万文件 / 3.5GB），
  // 而且 workspace 之间的自指链接还会成环。Node 从包里往上找一定能找到根的。
  const segments = rel.split('/')
  if (segments.indexOf('node_modules') > 0) return true
  return false
}

function countDir(dir) {
  stats.dirs += 1
  fs.mkdirSync(dir, { recursive: true })
}

function placeFile(src, dst) {
  fs.mkdirSync(path.dirname(dst), { recursive: true })
  const size = fs.statSync(src).size
  stats.bytes += size
  stats.files += 1
  try {
    fs.linkSync(src, dst)
    stats.hardlinked += 1
  } catch {
    fs.copyFileSync(src, dst)
    stats.copied += 1
  }
}

function copyReal(target, dst, stack) {
  const st = fs.statSync(target)
  if (st.isDirectory()) {
    countDir(dst)
    copyDir(target, dst, stack)
  } else {
    placeFile(target, dst)
  }
}

function copyEntry(src, dst, rel, stack) {
  const st = fs.lstatSync(src)

  if (st.isSymbolicLink()) {
    stats.links += 1
    let real
    try {
      real = fs.realpathSync(src)
    } catch {
      // 悬挂链接：目标已被裁掉（例如非 win32 平台包）。发行树里不要留链接。
      stats.dangling += 1
      return
    }
    if (stack.includes(real)) {
      stats.cycles += 1
      cyclePaths.push(rel)
      return
    }
    copyReal(real, dst, [...stack, real])
    return
  }

  if (st.isDirectory()) {
    countDir(dst)
    copyDir(src, dst, stack)
    return
  }

  placeFile(src, dst)
}

function copyDir(src, dst, stack) {
  for (const entry of fs.readdirSync(src, { withFileTypes: true })) {
    const childSrc = path.join(src, entry.name)
    const childDst = path.join(dst, entry.name)
    const rel = toPosix(path.relative(source, childSrc))
    if (isExcluded(rel)) continue
    copyEntry(childSrc, childDst, rel, stack)
  }
}

const rootReal = fs.realpathSync(source)
fs.mkdirSync(dest, { recursive: true })

for (const rel of includes) {
  const src = path.join(source, ...rel.split('/'))
  if (!fs.existsSync(src)) continue
  copyEntry(src, path.join(dest, ...rel.split('/')), rel, [rootReal])
}

console.log(JSON.stringify({ ...stats, sample: cyclePaths.slice(0, 5) }))

// 环在这里是「自己指向自己」式的 workspace 链接，例如
// node_modules/@deepseek-ai/cordis -> vendor/cordis，而 vendor/cordis 里又有
// 一条指回 @deepseek-ai/cordis 的链接。跳过这一条不会丢代码：目标包在祖先的
// node_modules 里一定有一份真的，Node 的向上查找会命中它。留空目录反而会把
// 查找挡住，所以这里什么都不建，只把数量报出来，由调用方的冒烟测试兜底。
if (stats.cycles > 0) {
  console.warn(`materialize: 跳过 ${stats.cycles} 条成环的链接（由祖先 node_modules 兜底）`)
}
if (stats.dangling > 0) {
  console.warn(`materialize: 跳过 ${stats.dangling} 条悬挂链接（目标已被裁掉）`)
}
