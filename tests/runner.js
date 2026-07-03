const isE2e = process.argv.includes('--e2e');
const runAll = isE2e
    ? (await import('../build/e2e/Tests.js')).runAll
    : (await import('../build/tests/Tests.js')).runAll;

runAll(process.argv.slice(2).filter(a => a !== '--e2e'))
    .then(code => process.exit(code))
    .catch(err => {
        console.error('RUNALL_FAILED:', err && err.message ? err.message : err);
        process.exit(2);
    });
