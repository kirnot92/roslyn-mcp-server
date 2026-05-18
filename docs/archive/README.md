# Documentation Archive

이 디렉터리는 현재 구현 컨텍스트에서 제외한 과거 문서를 보관한다.

기본 구현 세션에서는 archive를 읽지 않는다. 아래 상황에서만 필요한 파일을 직접 연다.

- 과거 milestone의 완료 범위를 감사해야 할 때
- 특정 설계 결정의 배경을 확인해야 할 때
- 예전 smoke 결과 원문이 필요할 때
- retired guide를 참고해야 할 때

구조:

- `plans/`: 완료되었거나 보류된 계획 문서
- `milestones/`: 과거 milestone 계획
- `smoke-tests/`: 과거 smoke 결과 원문
- `guides/`: 현재 기본 workflow에서 제외된 guide

활성 문서는 `docs/` 바로 아래에 둔다. 새 문서를 만들 때 archive의 오래된 milestone 서술을 다시 활성 문서로 복사하지 말고, 현재 계약과 다음 행동만 요약한다.
