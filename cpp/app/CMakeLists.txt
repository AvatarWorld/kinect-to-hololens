add_executable(KinectReader
  kinect_reader.cpp
  helper/opencv_helper.h
)
target_include_directories(KinectReader PRIVATE
  "${AZURE_KINECT_DIR}/sdk/include"
)
target_link_libraries(KinectReader
  ${OpenCV_LIBS}
  "${AZURE_KINECT_DIR}/sdk/windows-desktop/amd64/release/lib/k4a.lib"
  KinectToHololens
  ${Libvpx_LIB}
)
set_target_properties(KinectReader PROPERTIES
  CXX_STANDARD 17
)
add_custom_command(TARGET KinectReader POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E copy_if_different "${AZURE_KINECT_DIR}/sdk/windows-desktop/amd64/release/bin/depthengine_2_0.dll"
  $<TARGET_FILE_DIR:KinectReader>
)
add_custom_command(TARGET KinectReader POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E copy_if_different "${AZURE_KINECT_DIR}/sdk/windows-desktop/amd64/release/bin/k4a.dll"
  $<TARGET_FILE_DIR:KinectReader>
)

add_executable(KinectSender
  kinect_sender.cpp
  helper/kinect_helper.h
)
target_include_directories(KinectSender PRIVATE
  "${AZURE_KINECT_DIR}/sdk/include"
)
target_link_libraries(KinectSender
  "${AZURE_KINECT_DIR}/sdk/windows-desktop/amd64/release/lib/k4a.lib"
  KinectToHololens
  ${Libvpx_LIB}
)
set_target_properties(KinectSender PROPERTIES
  CXX_STANDARD 17
)
add_custom_command(TARGET KinectSender POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E copy_if_different "${AZURE_KINECT_DIR}/sdk/windows-desktop/amd64/release/bin/depthengine_2_0.dll"
  $<TARGET_FILE_DIR:KinectSender>
)
add_custom_command(TARGET KinectSender POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E copy_if_different "${AZURE_KINECT_DIR}/sdk/windows-desktop/amd64/release/bin/k4a.dll"
  $<TARGET_FILE_DIR:KinectSender>
)

add_executable(KinectReceiver
  kinect_receiver.cpp
  helper/opencv_helper.h
)
target_link_libraries(KinectReceiver
  ${OpenCV_LIBS}
  KinectToHololens
)
set_target_properties(KinectReceiver PROPERTIES
  CXX_STANDARD 17
)

add_executable(KinectListener
  kinect_listener.cpp
)
target_link_libraries(KinectListener
  KinectToHololens
  libsoundio::libsoundio
)
set_target_properties(KinectListener PROPERTIES
  CXX_STANDARD 17
)