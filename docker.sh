#!/bin/bash
set -euxo pipefail

docker build -t uniffi-bindgen-cs-test-runner .

docker run \
    -ti --rm \
    --volume $PWD:/mounted_workdir \
    --workdir /mounted_workdir \
    uniffi-bindgen-cs-test-runner bash
