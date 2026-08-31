import { createHash } from 'node:crypto'
import { readdirSync, readFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import { dirname, join, relative, sep } from 'node:path'

const DESCRIPTOR_PATH = '/.well-known/dsh-webui-compatibility.json'
const EXPECTED_PACKAGE_NAME = '@linxin666/dsh-remote-web-ui'
const EXPECTED_PACKAGE_VERSION = '0.3.10'
const EXPECTED_FILE_COUNT = 201
const EXPECTED_TREE_SHA256 =
  'bb3bc6f283a8d956e8e8b19ec1c16caaa2b6a4631143643fef0f06000cc226ce'

const descriptor = Buffer.from(
  JSON.stringify({
    schemaVersion: 1,
    contractVersion: '1.1.0',
    components: [
      {
        uiId: 'io.github.zhu1090093659.dsh-remote-web-ui',
        uiVersion: EXPECTED_PACKAGE_VERSION,
        sourceRev: EXPECTED_TREE_SHA256,
        adapterKeys: ['dsh-remote-web-ui.lan-pairing'],
      },
    ],
  }),
  'utf8',
)

export const name = 'dsh-remote-web-ui-compatibility'
export const inject = ['webServer']

export function apply(ctx) {
  const packageRoot = resolveReviewedPackageRoot()
  const identity = JSON.parse(readFileSync(join(packageRoot, 'package.json'), 'utf8'))
  if (
    identity.name !== EXPECTED_PACKAGE_NAME ||
    identity.version !== EXPECTED_PACKAGE_VERSION
  ) {
    throw new Error('dsh-remote-web-ui compatibility: package identity mismatch')
  }

  const attestation = packageTreeSha256(packageRoot)
  if (
    attestation.fileCount !== EXPECTED_FILE_COUNT ||
    attestation.sha256 !== EXPECTED_TREE_SHA256
  ) {
    throw new Error('dsh-remote-web-ui compatibility: package tree mismatch')
  }

  ctx.effect(
    () =>
      ctx.webServer.register({
        kind: 'exact',
        path: DESCRIPTOR_PATH,
        handler(req, res) {
          const method = req.method ?? 'GET'
          if (method !== 'GET' && method !== 'HEAD') {
            req.resume()
            res.writeHead(405, {
              allow: 'GET, HEAD',
              'cache-control': 'no-store',
            })
            res.end()
            return
          }

          res.writeHead(200, {
            'content-type': 'application/json; charset=utf-8',
            'content-length': String(descriptor.length),
            'cache-control': 'no-store',
            'x-content-type-options': 'nosniff',
          })
          res.end(method === 'HEAD' ? undefined : descriptor)
        },
      }),
    'dsh-remote-web-ui compatibility descriptor',
  )
}

function resolveReviewedPackageRoot() {
  const require = createRequire(import.meta.url)
  return dirname(require.resolve(`${EXPECTED_PACKAGE_NAME}/package.json`))
}

function packageTreeSha256(root) {
  const files = []
  visit(root, root, files)
  files.sort()

  const hash = createHash('sha256')
  for (const file of files) {
    hash.update(file, 'utf8')
    hash.update(Buffer.from([0]))
    hash.update(readFileSync(join(root, file)))
    hash.update(Buffer.from([0]))
  }

  return { fileCount: files.length, sha256: hash.digest('hex') }
}

function visit(root, directory, files) {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    if (entry.name === 'node_modules') continue
    const path = join(directory, entry.name)
    if (entry.isDirectory()) {
      visit(root, path, files)
    } else if (entry.isFile()) {
      files.push(relative(root, path).split(sep).join('/'))
    }
  }
}
