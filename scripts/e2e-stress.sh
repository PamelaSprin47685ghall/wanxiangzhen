#!/bin/bash
set -eo pipefail
for i in {1..20}; do
  echo "🦄 wxz e2e stress run $i/20"
  rm -f /tmp/wanxiangzhen-e2e.lock
  npm run e2e --quiet 2>&1 | tee /tmp/wxz-e2e-$i.log | tail -5
done
echo "✅ stress test passed: 20 e2e runs succeeded"
