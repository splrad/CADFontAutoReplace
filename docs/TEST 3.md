# 这是一个测试文件3



- 

- ```yaml
  日期: 2026-04-12
  tags:
    - CAD
    - SouthMap
    - CASS
  ```

  # 问题

  在软件中展绘高程点后，其显示尺寸与预期不符。

  ------

  # 解决方案

  - **特性缩放比例**：默认 0.5，同时调整高程点文字与圆点大小。

  警告

  不建议调整此项

  ![image.png](https://splrad-img.oss-cn-chengdu.aliyuncs.com/20260411112124647.jpg)

  - **参数设置字高**：分别设置“高程点字高”和“展点号字高”，仅影响文字大小。
     ![image.png](https://splrad-img.oss-cn-chengdu.aliyuncs.com/20260411112415861.jpg)
  - **高程点圆点大小**：通过修改图块 `gc200` 的参数，调整圆点及其填充的显示尺寸。
     ![image.png](https://splrad-img.oss-cn-chengdu.aliyuncs.com/20260413170828834.jpg)
